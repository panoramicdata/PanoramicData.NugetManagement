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
			.Should().Be("the repository is not green (1 failing besides VER-05)");

	[Fact]
	public void PublishingWithoutConfirmationShouldBeRefused()
		=> AgentApi.Refusal(WorkKind.Publish, Row(), confirm: false).Should().NotBeNull();
}
