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

	private static RuleResult Result(string ruleId, bool passed) => new()
	{
		RuleId = ruleId,
		RuleName = ruleId,
		Category = AssessmentCategory.ProjectMetadata,
		Severity = AssessmentSeverity.Error,
		Passed = passed,
		Message = passed ? "fine" : "not fine",
		Advisory = passed
			? null
			: new RuleAdvisory { Summary = "Do the thing", Detail = "At length" }
	};

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
			.Should().Equal(["META-04"]);

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

		candidates.Should().Equal(["CQ-03", "META-04", "META-05"],
			"a stable order makes the queue readable and the fan-out repeatable");
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
			.Should().OnlyContain(ruleId => _remediations.Get(ruleId) == null);
	}
}
