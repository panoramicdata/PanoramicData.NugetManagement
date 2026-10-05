using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiFixTargets"/>: which repositories have work only a local model can do. Lifted
/// out of the page so that deciding it for many repositories at once is testable.
/// </summary>
public class AiFixTargetsTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryIssue Issue(string author, int number = 1, bool isPullRequest = false, DateTimeOffset? lastMaintainerReplyUtc = null) => new()
	{
		LastMaintainerReplyUtc = lastMaintainerReplyUtc,
		Number = number,
		Title = "Missing methods",
		IsPullRequest = isPullRequest,
		HtmlUrl = $"https://github.com/panoramicdata/Sample/issues/{number}",
		AuthorLogin = author,
		CreatedAtUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
	};

	private static RepositoryDashboardRow Row(params RepositoryIssue[] issues) => new()
	{
		Organization = "panoramicdata",
		RepositoryFullName = "panoramicdata/Sample",
		OpenIssues = [.. issues]
	};

	[Fact]
	public void AHumanIssueNobodyHasAnalysed_IsWork()
		=> AiFixTargets.UnanalysedHumanIssues(Row(Issue("a-person")))
			.Should().ContainSingle();

	[Fact]
	public void ABotsItem_IsNotWork()
		=> AiFixTargets.UnanalysedHumanIssues(Row(Issue("dependabot[bot]")))
			.Should().BeEmpty("Dependabot's belong to triage, which does not need a model");

	[Fact]
	public void APullRequest_IsNotWork()
		=> AiFixTargets.UnanalysedHumanIssues(Row(Issue("a-person", isPullRequest: true)))
			.Should().BeEmpty();

	[Fact]
	public void AVerdictWhoseConversationMovedOn_IsStale()
	{
		var issue = Issue("a-person", lastMaintainerReplyUtc: new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero));
		issue.AnalysedAtIssueUpdatedUtc = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

		AiFixTargets.IsVerdictStale(issue).Should().BeTrue(
			"a comment after the verdict may be the reporter answering the very question that made it escalate");
	}

	[Fact]
	public void AVerdictNoOneHasAnsweredSince_IsNotStale()
	{
		var issue = Issue("a-person", lastMaintainerReplyUtc: new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));
		issue.AnalysedAtIssueUpdatedUtc = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero);

		AiFixTargets.IsVerdictStale(issue).Should().BeFalse();
	}

	[Fact]
	public void ARepositoryWithNothingForTheModel_HasNoWork()
		=> AiFixTargets.WorkCount(Row(), new RemediationRegistry())
			.Should().Be(0, "no assessment and no issues: there is nothing here the model could do");

	[Fact]
	public void WorkCount_AddsUnanalysedIssuesToRulesOnlyTheModelCanFix()
	{
		var row = Row(Issue("a-person"), Issue("another-person", number: 2));
		row.IsClonedLocally = true;
		row.LocalPath = Path.GetTempPath();
		row.Assessment = new RepoAssessment
		{
			RepositoryFullName = row.RepositoryFullName,
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults =
			[
				new RuleResult
				{
					RuleId = "ZZ-99",
					RuleName = "A rule with no deterministic remediation",
					Category = AssessmentCategory.CodeQuality,
					Severity = AssessmentSeverity.Error,
					Passed = false,
					Message = "wrong"
				}
			]
		};

		AiFixTargets.WorkCount(row, new RemediationRegistry())
			.Should().BeGreaterThan(2, "two issues, plus at least one candidate for the unremediable rule");
	}
}
