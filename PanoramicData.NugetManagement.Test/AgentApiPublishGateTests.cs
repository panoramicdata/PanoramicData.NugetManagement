using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// The agent API's publish gate: green, clean and in step, except for the rule a publish itself clears.
/// </summary>
public class AgentApiPublishGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RuleResult Failing(string ruleId) => new()
	{
		RuleId = ruleId,
		RuleName = ruleId,
		Category = AssessmentCategory.Versioning,
		Severity = AssessmentSeverity.Warning,
		Passed = false,
		Message = "failing"
	};

	private static RepositoryDashboardRow Row(params RuleResult[] results) => new()
	{
		RepositoryFullName = "panoramicdata/Sample",
		IsClonedLocally = true,
		IsWorkingTreeClean = true,
		HasUnpushedCommits = false,
		IsSyncedWithOrigin = true,
		Assessment = new RepoAssessment
		{
			RepositoryFullName = "panoramicdata/Sample",
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = [.. results]
		}
	};

	[Fact]
	public void AGreenRepositoryShouldBePublishable()
		=> AgentApi.Refusal(WorkKind.Publish, Row(), confirm: true).Should().BeNull();

	[Fact]
	public void ARepositoryFailingOnlyTheUnreleasedWorkRuleShouldBePublishable()
		=> AgentApi.Refusal(WorkKind.Publish, Row(Failing("VER-05")), confirm: true).Should().BeNull();

	[Fact]
	public void ARepositoryFailingAnyOtherRuleShouldBeRefused()
		=> AgentApi.Refusal(WorkKind.Publish, Row(Failing("VER-05"), Failing("CQ-06")), confirm: true)
			.Should().Be("the repository is not green (1 failing besides VER-05, CI-11, CI-13)");

	[Theory]
	[InlineData("CI-11")]
	[InlineData("CI-13")]
	public void ARepositoryWhoseOnlyFailureIsItsReleaseStateShouldBePublishable(string ruleId)
		=> AgentApi.Refusal(WorkKind.Publish, Row(Failing(ruleId)), confirm: true).Should().BeNull("a new release is what clears it");

	[Fact]
	public void AnOpenGitHubIssueShouldNotStopARepositoryFromBeingPublished()
	{
		var row = Row(Failing("VER-05"));
		row.OpenIssues =
		[
			new RepositoryIssue
			{
				Number = 22,
				Title = "A real bug a release can fix",
				HtmlUrl = "https://github.com/panoramicdata/Sample/issues/22",
				AuthorLogin = "someone",
				CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-40)
			}
		];

		row.StaleIssues.Should().ContainSingle("the issue has gone unanswered for forty days");
		AgentApi.Refusal(WorkKind.Publish, row, confirm: true).Should().BeNull("an unanswered issue says nothing about whether the package is safe to ship");
	}

	[Fact]
	public void ARepositoryWithUnpushedCommitsShouldBeRefused()
	{
		var row = Row();
		row.HasUnpushedCommits = true;

		AgentApi.Refusal(WorkKind.Publish, row, confirm: true).Should().Be("the working tree is not clean and in step with origin");
	}

	[Fact]
	public void PublishingWithoutConfirmationShouldBeRefused()
		=> AgentApi.Refusal(WorkKind.Publish, Row(), confirm: false).Should().NotBeNull();
}
