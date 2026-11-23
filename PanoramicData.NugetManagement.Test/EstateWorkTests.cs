using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="EstateWork"/>: an estate-wide press queues only repositories with known work,
/// and its tooltip says what that work is.
/// </summary>
public class EstateWorkTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

	private static bool Remediable(RuleResult result) => result.RuleId is "COM-01" or "REPO-06";

	private static RuleResult Failing(string ruleId) => new()
	{
		RuleId = ruleId,
		RuleName = ruleId,
		Category = AssessmentCategory.RepositoryHygiene,
		Severity = AssessmentSeverity.Warning,
		Passed = false,
		Message = "failing"
	};

	private static RepositoryIssue DependabotPullRequest(int number) => new()
	{
		Number = number,
		Title = "Bump something",
		IsPullRequest = true,
		HtmlUrl = $"https://github.com/panoramicdata/x/pull/{number}",
		AuthorLogin = "dependabot[bot]",
		CreatedAtUtc = _now
	};

	private static RepositoryDashboardRow Row(string name, params RuleResult[] failures) => new()
	{
		RepositoryFullName = $"panoramicdata/{name}",
		Organization = "panoramicdata",
		IsClonedLocally = true,
		CurrentBranch = "main",
		IsSyncedWithOrigin = true,
		SyncStatusCheckedAtUtc = _now,
		IsWorkingTreeClean = true,
		HasUnpushedCommits = false,
		LastBuildState = RepositoryBuildState.Succeeded,
		LastTestState = RepositoryTestState.Passed,
		OpenIssuesKnown = true,
		Assessment = new RepoAssessment
		{
			RepositoryFullName = $"panoramicdata/{name}",
			DefaultBranch = "main",
			AssessedAtUtc = _now,
			RuleResults = [.. failures]
		}
	};

	private static bool HasWork(WorkflowStep step, RepositoryDashboardRow row)
		=> EstateWork.HasWork(step, row, Remediable, _now);

	[Theory]
	[InlineData(WorkflowStep.Fix)]
	[InlineData(WorkflowStep.GitSync)]
	[InlineData(WorkflowStep.CommitAndPush)]
	public void ARepositoryKnownToBeDone_HasNoWork(WorkflowStep step)
		=> HasWork(step, Row("Done")).Should().BeFalse();

	[Theory]
	[InlineData(WorkflowStep.Reassess)]
	[InlineData(WorkflowStep.Build)]
	[InlineData(WorkflowStep.Test)]
	public void StepsWithNoTrustworthyDoneState_AlwaysHaveWork(WorkflowStep step)
		=> HasWork(step, Row("Done")).Should().BeTrue("a remembered build or test result is not cleared by outside edits");

	[Fact]
	public void AFailingRuleWithARemediation_IsFixWork()
		=> HasWork(WorkflowStep.Fix, Row("A", Failing("COM-01"))).Should().BeTrue();

	[Fact]
	public void AFailingRuleWithoutARemediation_IsNotFixWork()
		=> HasWork(WorkflowStep.Fix, Row("A", Failing("META-04"))).Should().BeFalse();

	[Fact]
	public void ADependabotPullRequest_IsFixWork()
	{
		var row = Row("A");
		row.OpenIssues = [DependabotPullRequest(1)];

		HasWork(WorkflowStep.Fix, row).Should().BeTrue();
	}

	[Fact]
	public void AnUnreadInbox_IsNotFixWork()
	{
		var row = Row("A");
		row.OpenIssues = [DependabotPullRequest(1)];
		row.OpenIssuesKnown = false;

		HasWork(WorkflowStep.Fix, row).Should().BeFalse();
	}

	[Fact]
	public void RemediationsOffMain_AreBlockedNotQueued()
	{
		var row = Row("A", Failing("COM-01"));
		row.CurrentBranch = "feature";

		HasWork(WorkflowStep.Fix, row).Should().BeFalse();
		EstateWork.HasBlockedRemediations(row, Remediable).Should().BeTrue();
	}

	[Fact]
	public void TheMasterToMainRemediation_RunsOffMain()
	{
		var row = Row("A", Failing("REPO-06"), Failing("COM-01"));
		row.CurrentBranch = "master";

		EstateWork.RemediationCount(row, Remediable).Should().Be(2);
	}

	[Fact]
	public void AnExpiredSyncAnswer_IsSyncWork()
	{
		var row = Row("A");
		row.SyncStatusCheckedAtUtc = _now - TimeSpan.FromHours(1);

		HasWork(WorkflowStep.GitSync, row).Should().BeTrue("an expired answer is not knowledge");
	}

	[Fact]
	public void UncommittedChanges_AreCommitWork()
	{
		var row = Row("A");
		row.IsWorkingTreeClean = false;

		HasWork(WorkflowStep.CommitAndPush, row).Should().BeTrue();
	}

	[Fact]
	public void TheFixTooltip_SaysWhatItWillDoAndWhatItLeavesOut()
	{
		var fixing = Row("Fixing", Failing("COM-01"));
		var triaging = Row("Triaging");
		triaging.OpenIssues = [DependabotPullRequest(1), DependabotPullRequest(2)];
		var done = Row("Done");

		EstateWork.Describe(WorkflowStep.Fix, "Fix", [fixing, triaging, done], 4, Remediable, _now)
			.Should().Be("Fix will apply 1 auto-fix in Fixing, and triage 2 Dependabot pull requests in Triaging. "
				+ "1 repository has nothing to fix. "
				+ "1 ticked repository skipped: excluded, not governed, or not cloned locally.");
	}

	[Fact]
	public void TheFixTooltip_SeparatesBlockedAndUnassessedFromDone()
	{
		var offMain = Row("OffMain", Failing("COM-01"));
		offMain.CurrentBranch = "feature";
		var unassessed = Row("Unassessed");
		unassessed.Assessment = null;

		EstateWork.Describe(WorkflowStep.Fix, "Fix", [offMain, unassessed, Row("Done")], 3, Remediable, _now)
			.Should().Be("Fix has nothing to do in the 3 ticked repositories. "
				+ "Auto-fixes in OffMain wait until it is on main and not behind origin. "
				+ "1 repository is not fully assessed yet, so Fix cannot tell what to do there: Re-assess first. "
				+ "1 repository has nothing to fix.");
	}

	[Fact]
	public void TheSyncTooltip_NamesTheRepositoriesItWillSync()
	{
		var stale = Row("Stale");
		stale.SyncStatusCheckedAtUtc = null;

		EstateWork.Describe(WorkflowStep.GitSync, "Git Sync", [stale, Row("Synced")], 2, Remediable, _now)
			.Should().Be("Git Sync will fetch, rebase and push 1 repository not recently confirmed in sync with origin: Stale. "
				+ "1 repository is already in sync with origin.");
	}

	[Fact]
	public void NothingToDo_SaysSo()
		=> EstateWork.Describe(WorkflowStep.CommitAndPush, "Commit & Push", [Row("A"), Row("B")], 2, Remediable, _now)
			.Should().Be("Commit & Push has nothing to do in the 2 ticked repositories. "
				+ "2 repositories have nothing to commit or push.");

	[Fact]
	public void NothingTicked_AsksForTicks()
		=> EstateWork.Describe(WorkflowStep.Build, "Build", [], 0, Remediable, _now)
			.Should().Be("Build: tick repositories to act on them.");
}
