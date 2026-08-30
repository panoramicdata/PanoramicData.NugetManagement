using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that stale issues count as repository failures, and that fresh ones do not.
/// </summary>
public class RepositoryIssueRollupTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryIssue Aged(int number, int daysSinceReply)
		=> new()
		{
			Number = number,
			Title = $"Item {number}",
			HtmlUrl = $"https://github.com/panoramicdata/Sample/issues/{number}",
			AuthorLogin = "reporter",
			CreatedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(400),
			LastMaintainerReplyUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(daysSinceReply)
		};

	private static RepositoryDashboardRow Row(RepoAssessment? assessment, params RepositoryIssue[] issues)
		=> new()
		{
			RepositoryFullName = "panoramicdata/Sample",
			Assessment = assessment,
			OpenIssues = [.. issues]
		};

	private static RepoAssessment CleanAssessment()
		=> new()
		{
			RepositoryFullName = "panoramicdata/Sample",
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = []
		};

	[Fact]
	public void AFreshIssueIsNotAFailure()
	{
		var row = Row(CleanAssessment(), Aged(1, daysSinceReply: 1));

		row.TotalFailures.Should().Be(0);
		row.TotalErrors.Should().Be(0);
		row.TotalCriticals.Should().Be(0);
		row.HealthStatus.Should().Be(PackageHealthStatus.Success);
	}

	[Fact]
	public void AWeekOldIssueIsAnErrorFailure()
	{
		var row = Row(CleanAssessment(), Aged(1, daysSinceReply: 8));

		row.TotalFailures.Should().Be(1);
		row.TotalErrors.Should().Be(1);
		row.TotalCriticals.Should().Be(0);
		row.HealthStatus.Should().Be(PackageHealthStatus.Error);
	}

	[Fact]
	public void AMonthOldIssueIsACriticalFailure()
	{
		var row = Row(CleanAssessment(), Aged(1, daysSinceReply: 45));

		row.TotalFailures.Should().Be(1);
		row.TotalCriticals.Should().Be(1);
		row.TotalErrors.Should().Be(0);
		row.HealthStatus.Should().Be(PackageHealthStatus.Error);
	}

	[Fact]
	public void IssueFailuresAddToRuleFailuresRatherThanReplacingThem()
	{
		var assessment = new RepoAssessment
		{
			RepositoryFullName = "panoramicdata/Sample",
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults =
			[
				new RuleResult
				{
					RuleId = "PKG-01",
					RuleName = "Package id set",
					Category = AssessmentCategory.ProjectMetadata,
					Severity = AssessmentSeverity.Error,
					Passed = false,
					Message = "missing"
				}
			]
		};

		var row = Row(assessment, Aged(1, daysSinceReply: 45), Aged(2, daysSinceReply: 2));

		row.TotalFailures.Should().Be(2, "one failing rule and one critical issue; the fresh issue is neither");
		row.TotalErrors.Should().Be(1);
		row.TotalCriticals.Should().Be(1);
	}

	[Fact]
	public void AnUnassessedRepositoryStaysUnknownHoweverStaleItsIssues()
	{
		var row = Row(assessment: null, Aged(1, daysSinceReply: 90));

		row.HealthStatus.Should().Be(PackageHealthStatus.Unknown,
			"not assessed is not the same as assessed and bad");
	}

	[Fact]
	public void ARowWithNoIssuesBehavesExactlyAsBefore()
	{
		var row = Row(CleanAssessment());

		row.OpenIssues.Should().BeEmpty();
		row.TotalFailures.Should().Be(0);
		row.HealthStatus.Should().Be(PackageHealthStatus.Success);
	}
}
