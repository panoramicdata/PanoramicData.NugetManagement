using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiFixCandidates"/>: which of a repository's failures belong to Fix with AI.
/// </summary>
/// <remarks>
/// This is what keeps the two buttons disjoint at run time, as the playbook test keeps them disjoint at
/// design time. It also decides whether the button is offered at all, so getting it wrong either hides
/// work that could be done or offers work that cannot.
/// </remarks>
public class AiFixCandidateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly RemediationRegistry _remediations = new();

	private static RuleResult Result(string ruleId, bool passed, params string[] targets) => new()
	{
		RuleId = ruleId,
		RuleName = ruleId,
		Category = AssessmentCategory.ProjectMetadata,
		Severity = AssessmentSeverity.Error,
		Passed = passed,
		Message = passed ? "fine" : "not fine",
		Advisory = passed
			? null
			: new RuleAdvisory
			{
				Summary = "Do the thing",
				Detail = "At length",
				Targets = targets.Length == 0
					? null
					: [.. targets.Select(path => new AdvisoryTarget(path, $"{path} is wrong.", $"Fix {path}."))]
			}
	};

	private static AiFixTarget Rule(string ruleId) => new(ruleId, null);

	private static RepositoryDashboardRow Row(params RuleResult[] results) => new()
	{
		RepositoryFullName = "panoramicdata/Sample",
		Organization = "panoramicdata",
		IsClonedLocally = true,
		LocalPath = @"C:\clones\Sample",
		Assessment = new RepoAssessment
		{
			RepositoryFullName = "panoramicdata/Sample",
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = [.. results]
		}
	};

	[Fact]
	public void AFailingRuleWithNoRemediation_IsACandidate()
		=> AiFixCandidates.For(Row(Result("META-04", passed: false)), _remediations)
			.Should().Equal([Rule("META-04")]);

	[Fact]
	public void AFailingRuleThatHasARemediation_IsNot()
		=> AiFixCandidates.For(Row(Result("COM-01", passed: false)), _remediations)
			.Should().BeEmpty("COM-01 has a remediation, so it belongs to Fix and not to Fix with AI");

	[Fact]
	public void APassingRule_IsNot()
		=> AiFixCandidates.For(Row(Result("META-04", passed: true)), _remediations)
			.Should().BeEmpty();

	[Fact]
	public void ARepositoryWithNoAssessment_HasNoCandidates()
	{
		var row = Row();
		row.Assessment = null;

		AiFixCandidates.For(row, _remediations).Should().BeEmpty(
			"without an assessment there is no evidence anything is wrong");
	}

	[Fact]
	public void ARepositoryThatIsNotClonedLocally_HasNoCandidates()
	{
		var row = Row(Result("META-04", passed: false));
		row.IsClonedLocally = false;

		AiFixCandidates.For(row, _remediations).Should().BeEmpty(
			"the model edits files on disk, so there has to be a working tree to edit");
	}

	[Fact]
	public void CandidatesAreOrderedAndDistinct()
	{
		var candidates = AiFixCandidates.For(
			Row(
				Result("META-05", passed: false),
				Result("META-04", passed: false),
				Result("CQ-03", passed: false)),
			_remediations);

		candidates.Should().Equal([Rule("CQ-03"), Rule("META-04"), Rule("META-05")],
			"a stable order makes the queue readable and the fan-out repeatable");
	}

	[Fact]
	public void AFailureThatNamesTargets_BecomesOneCandidatePerFile()
	{
		// The turn budget is per session. "Improve these three files" spends the first third of it
		// planning all three and finishes none; one file per session is one goal per budget.
		var candidates = AiFixCandidates.For(
			Row(Result("CQ-06", passed: false, "Publish.ps1", "src/VtlParser.cs")),
			_remediations);

		candidates.Should().Equal([
			new AiFixTarget("CQ-06", "Publish.ps1"),
			new AiFixTarget("CQ-06", "src/VtlParser.cs")]);
	}

	[Fact]
	public void AFailureThatNamesNoTargets_StaysOneCandidateForTheRule()
		=> AiFixCandidates.For(Row(Result("META-04", passed: false)), _remediations)
			.Should().Equal([Rule("META-04")],
				"nearly every rule's fix is one piece of work, and splitting it would be inventing files");

	[Fact]
	public void TwoRulesNamingTheSameFile_AreTwoCandidates()
	{
		// Distinctness is on the pair. Collapsing these to one would silently drop a rule's only chance
		// at that file.
		var candidates = AiFixCandidates.For(
			Row(
				Result("CQ-05", passed: false, "Publish.ps1"),
				Result("CQ-06", passed: false, "Publish.ps1")),
			_remediations);

		candidates.Should().Equal([
			new AiFixTarget("CQ-05", "Publish.ps1"),
			new AiFixTarget("CQ-06", "Publish.ps1")]);
	}

	/// <summary>
	/// Every candidate the real rule set can produce is one the AI path is genuinely responsible for —
	/// the complement of what RemediationRegistry covers.
	/// </summary>
	[Fact]
	public void NoCandidateOverlapsWithADeterministicRemediation()
	{
		var everyRuleFailing = Row([.. PanoramicData.NugetManagement.Services.RuleRegistry.Rules
			.Select(rule => Result(rule.RuleId, passed: false))]);

		AiFixCandidates.For(everyRuleFailing, _remediations)
			.Should().OnlyContain(target => _remediations.Get(target.RuleId) == null);
	}
}
