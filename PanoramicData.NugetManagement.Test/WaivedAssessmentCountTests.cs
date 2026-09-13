using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests how waived rules are counted. A waiver has to be visible to be auditable, so it must not
/// simply disappear into the passing tally — the board should be able to say how many rules this
/// repository is excused from, and how many of those excuses are no longer earning their keep.
/// </summary>
public class WaivedAssessmentCountTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void AWaivedFailureShouldNotBeCountedAsAFailure()
	{
		var assessment = AssessmentOf(Waived("HTTP-01", underlyingPassed: false), Failing("CI-01"));

		assessment.FailedCount.Should().Be(1);
	}

	[Fact]
	public void AWaivedFailureShouldNotMakeARepositoryNonCompliant()
	{
		var assessment = AssessmentOf(Waived("CI-01", underlyingPassed: false, AssessmentSeverity.Error));

		assessment.IsCompliant.Should().BeTrue();
	}

	[Fact]
	public void WaivedRulesShouldBeCountedSeparately()
	{
		var assessment = AssessmentOf(
			Waived("HTTP-01", underlyingPassed: false),
			Waived("CQ-05", underlyingPassed: true),
			Failing("CI-01"));

		assessment.WaivedCount.Should().Be(2);
	}

	[Fact]
	public void AWaiverShouldBeCountedStaleOnceItsRulePassesAnyway()
	{
		var assessment = AssessmentOf(
			Waived("HTTP-01", underlyingPassed: false),
			Waived("CQ-05", underlyingPassed: true));

		assessment.StaleWaiverCount.Should().Be(1);
	}

	[Fact]
	public void ACategoryShouldReportItsWaivedRules()
	{
		var summaries = DashboardService.BuildCategorySummaries(
		[
			Waived("HTTP-01", underlyingPassed: false),
			Failing("CI-01")
		]);

		summaries[AssessmentCategory.HttpClient].Waived.Should().Be(1);
	}

	[Fact]
	public void AWaivedRuleShouldNotBeCountedAmongTheRulesThatPass()
	{
		// Otherwise "3 passed" quietly includes a rule the repository is only excused from, and the
		// category looks healthier than it is.
		var summaries = DashboardService.BuildCategorySummaries(
		[
			Waived("HTTP-01", underlyingPassed: false),
			Passing("HTTP-02")
		]);

		summaries[AssessmentCategory.HttpClient].Passed.Should().Be(1);
	}

	private static RepoAssessment AssessmentOf(params RuleResult[] results) => new()
	{
		RepositoryFullName = "panoramicdata/LanSweeper.Api",
		DefaultBranch = "main",
		AssessedAtUtc = DateTimeOffset.UtcNow,
		RuleResults = [.. results]
	};

	private static RuleResult Waived(
		string ruleId,
		bool underlyingPassed,
		AssessmentSeverity severity = AssessmentSeverity.Info) => new()
		{
			RuleId = ruleId,
			RuleName = ruleId,
			Category = AssessmentCategory.HttpClient,
			Severity = severity,
			Passed = true,
			Message = "Waived: because.",
			Waiver = new AppliedWaiver
			{
				Waiver = new RuleWaiver { RuleId = ruleId, Reason = "because." },
				UnderlyingPassed = underlyingPassed,
				UnderlyingMessage = underlyingPassed ? "All good." : "Not good."
			}
		};

	private static RuleResult Failing(string ruleId) => new()
	{
		RuleId = ruleId,
		RuleName = ruleId,
		Category = AssessmentCategory.CiCd,
		Severity = AssessmentSeverity.Error,
		Passed = false,
		Message = "Not good."
	};

	private static RuleResult Passing(string ruleId) => new()
	{
		RuleId = ruleId,
		RuleName = ruleId,
		Category = AssessmentCategory.HttpClient,
		Severity = AssessmentSeverity.Info,
		Passed = true,
		Message = "All good."
	};
}
