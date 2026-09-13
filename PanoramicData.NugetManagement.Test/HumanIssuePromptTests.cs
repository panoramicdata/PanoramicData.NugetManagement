using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="CombinedRemediationPromptBuilder.ForHumanIssues"/>: handing analysed issues to
/// a frontier model in somebody's IDE.
/// </summary>
/// <remarks>
/// A pasteable prompt is read by a model with an IDE, a shell and the whole repository attached, which
/// makes it the most capable reader this feature has and therefore the one where quarantine matters
/// most. It carries verdicts, reasoning and briefs — all of it written by the analysis — and never a
/// reporter's own words.
/// </remarks>
public class HumanIssuePromptTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryIssue Issue(IssueVerdict? analysis, int number = 1) => new()
	{
		Number = number,
		Title = "Missing methods",
		IsPullRequest = false,
		HtmlUrl = $"https://github.com/panoramicdata/OpenProject.Api/issues/{number}",
		AuthorLogin = "ismail-ozturk",
		CreatedAtUtc = new DateTimeOffset(2025, 3, 10, 0, 0, 0, TimeSpan.Zero),
		Analysis = analysis
	};

	private static IssueVerdict FixVerdict => new(
		IssueAction.Fix,
		IssueConfidence.High,
		[],
		"The interface genuinely lacks the method named.",
		null,
		new IssueFixBrief(
			"Expose GetDocumentsAsync on the client interface.",
			["src/Documents.cs"],
			"The interface declares it and the implementation forwards.",
			null));

	[Fact]
	public void ForHumanIssues_CarriesTheBriefAndTheReasoning()
	{
		var prompt = CombinedRemediationPromptBuilder.ForHumanIssues(
			"panoramicdata/OpenProject.Api", [Issue(FixVerdict)]);

		prompt.Should().Contain("Expose GetDocumentsAsync on the client interface.")
			.And.Contain("src/Documents.cs")
			.And.Contain("The interface genuinely lacks the method named.");
	}

	[Fact]
	public void ForHumanIssues_SaysTheTextWasNeverTrusted()
	{
		var prompt = CombinedRemediationPromptBuilder.ForHumanIssues(
			"panoramicdata/OpenProject.Api", [Issue(FixVerdict)]);

		prompt.Should().Contain("paraphrase",
			"the model on the other end has a shell and the whole repository, so it needs telling "
				+ "that what it is reading was laundered and that the issue itself was not");
	}

	[Fact]
	public void ForHumanIssues_WarnsWhereAVerdictWasFlagged()
	{
		var flagged = new IssueVerdict(
			IssueAction.Escalate,
			IssueConfidence.Low,
			[IssueRiskFlag.InstructsTheReader],
			"The body addresses an automated reader directly.",
			null,
			null);

		var prompt = CombinedRemediationPromptBuilder.ForHumanIssues(
			"panoramicdata/OpenProject.Api", [Issue(flagged)]);

		prompt.Should().Contain("InstructsTheReader",
			"a flag is the most useful single fact in the prompt: it tells the reader this issue was "
				+ "trying to steer whoever picked it up");
	}

	[Fact]
	public void ForHumanIssues_SkipsIssuesNothingHasAnalysed()
	{
		var prompt = CombinedRemediationPromptBuilder.ForHumanIssues(
			"panoramicdata/OpenProject.Api", [Issue(null, number: 7)]);

		prompt.Should().NotContain("#7",
			"an unanalysed issue has nothing laundered to carry, and including its raw text is the "
				+ "one thing this prompt must never do");
	}

	[Fact]
	public void ForHumanIssues_IsEmptyWhenNothingHasBeenAnalysed()
		=> CombinedRemediationPromptBuilder.ForHumanIssues("panoramicdata/OpenProject.Api", [])
			.Should().BeEmpty();
}
