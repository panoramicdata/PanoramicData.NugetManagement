using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="IssueAutonomyGate"/>: what the application may do about a human-raised issue
/// without a human having read the verdict first.
/// </summary>
/// <remarks>
/// The gate exists because the model classifies and C# decides. Every test here asserts the same
/// property from a different angle — a risk flag, a low confidence or an unknown author can only ever
/// <em>remove</em> autonomy, and nothing the model says about itself can grant it.
/// </remarks>
public class IssueAutonomyGateTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("issue-gate-").FullName;

	public IssueAutonomyGateTests WithFile(string relativePath)
	{
		var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, "// content");
		return this;
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private static IssueVerdict FixVerdict(
		IssueConfidence confidence = IssueConfidence.High,
		IReadOnlyList<IssueRiskFlag>? risks = null,
		IReadOnlyList<string>? paths = null)
		=> new(
			IssueAction.Fix,
			confidence,
			risks ?? [],
			"The reporter names a method the interface genuinely lacks.",
			null,
			new IssueFixBrief(
				"Expose the missing method on the document client.",
				paths ?? ["src/Documents.cs"],
				"The interface declares the method and the implementation forwards to it.",
				null));

	[Fact]
	public void MayFixAutomatically_AllowsAConfidentUnflaggedFixFromAKnownContributor()
	{
		WithFile("src/Documents.cs");

		var decision = IssueAutonomyGate.MayFixAutomatically(FixVerdict(), "Contributor", _root);

		decision.Allowed.Should().BeTrue(
			"this is the boring case the feature exists to automate: a confident, unflagged fix to "
				+ "one ordinary source file, raised by somebody whose code has already been taken");
	}

	[Fact]
	public void MayFixAutomatically_BlocksWhenConfidenceIsNotHigh()
	{
		WithFile("src/Documents.cs");

		var decision = IssueAutonomyGate.MayFixAutomatically(
			FixVerdict(confidence: IssueConfidence.Medium), "Contributor", _root);

		decision.Allowed.Should().BeFalse(
			"a model that is only fairly sure it understood a stranger's report is describing "
				+ "exactly the case a human should see first");
	}

	[Fact]
	public void MayFixAutomatically_BlocksOnASingleRiskFlag()
	{
		WithFile("src/Documents.cs");

		var decision = IssueAutonomyGate.MayFixAutomatically(
			FixVerdict(risks: [IssueRiskFlag.ContainsExternalLinks]), "Contributor", _root);

		decision.Allowed.Should().BeFalse(
			"the bar is zero flags rather than no severe ones — grading severity would put the "
				+ "model back in charge of the gate that exists to constrain it");
	}

	[Fact]
	public void MayFixAutomatically_BlocksWhenAPathFailsTheScreen()
	{
		WithFile(".github/workflows/publish.yml");

		var decision = IssueAutonomyGate.MayFixAutomatically(
			FixVerdict(paths: [".github/workflows/publish.yml"]), "Owner", _root);

		decision.Allowed.Should().BeFalse(
			"a confident verdict from a known author must not buy access to the publish workflow");
	}

	[Fact]
	public void MayFixAutomatically_BlocksWhenTheFixSpreadsBeyondThreeFiles()
	{
		WithFile("src/A.cs").WithFile("src/B.cs").WithFile("src/C.cs").WithFile("src/D.cs");

		var decision = IssueAutonomyGate.MayFixAutomatically(
			FixVerdict(paths: ["src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs"]), "Member", _root);

		decision.Allowed.Should().BeFalse(
			"breadth is the signal that the model has understood something larger than one report, "
				+ "and breadth is also what makes a bad fix expensive to unpick");
	}

	[Fact]
	public void MayFixAutomatically_BlocksAnAuthorTheProjectHasNoHistoryWith()
	{
		WithFile("src/Documents.cs");

		var decision = IssueAutonomyGate.MayFixAutomatically(FixVerdict(), "None", _root);

		decision.Allowed.Should().BeFalse(
			"acting unattended on a first-time stranger's text is the case with the least evidence "
				+ "behind it and the most reason for someone to have crafted it");
	}

	[Fact]
	public void MayFixAutomatically_BlocksAVerdictThatIsNotAFix()
	{
		WithFile("src/Documents.cs");

		var verdict = FixVerdict() with { Action = IssueAction.Escalate };

		var decision = IssueAutonomyGate.MayFixAutomatically(verdict, "Owner", _root);

		decision.Allowed.Should().BeFalse(
			"Escalate means the model asked for a human; honouring the brief anyway would make the "
				+ "request meaningless");
	}

	[Fact]
	public void MayAnswerAutomatically_AllowsAConfidentUnflaggedAnswer()
	{
		var verdict = new IssueVerdict(
			IssueAction.Answer,
			IssueConfidence.High,
			[],
			"The reporter is calling the synchronous overload and expecting it to page.",
			"This is the documented behaviour of that overload.",
			null);

		IssueAutonomyGate.MayAnswerAutomatically(verdict).Allowed.Should().BeTrue(
			"explaining a misunderstanding is the cheapest correct outcome and costs nothing to "
				+ "get wrong beyond a follow-up comment");
	}

	[Fact]
	public void MayAnswerAutomatically_BlocksWhenTheTextTriesToInstructItsReader()
	{
		var verdict = new IssueVerdict(
			IssueAction.Answer,
			IssueConfidence.High,
			[IssueRiskFlag.InstructsTheReader],
			"The body addresses an automated reader directly.",
			"drafted",
			null);

		IssueAutonomyGate.MayAnswerAutomatically(verdict).Allowed.Should().BeFalse(
			"a reply is the one action with a public voice, so text written to steer the replier is "
				+ "disqualifying rather than merely noted");
	}

	[Theory]
	[InlineData(IssueAction.Reject)]
	[InlineData(IssueAction.Escalate)]
	public void NeitherGate_EverAllowsRejectionOrEscalation(IssueAction action)
	{
		var verdict = new IssueVerdict(
			action, IssueConfidence.High, [], "certain", "drafted", null);

		IssueAutonomyGate.MayAnswerAutomatically(verdict).Allowed.Should().BeFalse();
		IssueAutonomyGate.MayFixAutomatically(verdict, "Owner", _root).Allowed.Should().BeFalse(
			"closing a real person's issue, or declaring it an attack, is published under the "
				+ "organisation's name and is never worth automating");
	}
}
