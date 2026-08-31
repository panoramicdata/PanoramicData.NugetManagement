using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotIssueSynthesizer"/>: turning a repository's open Dependabot pull
/// requests into findings the issue-centric tree can group, so one dependency can be cleared across
/// the whole estate in a single action.
/// </summary>
public class DependabotIssueSynthesizerTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryIssue PullRequest(
		string title,
		int number = 1,
		string author = "dependabot[bot]",
		bool isPullRequest = true,
		int ageDays = 400)
		=> new()
		{
			Number = number,
			Title = title,
			IsPullRequest = isPullRequest,
			HtmlUrl = $"https://github.com/panoramicdata/Sample/pull/{number}",
			AuthorLogin = author,
			CreatedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(ageDays)
		};

	private static RepositoryDashboardRow Row(params RepositoryIssue[] issues) => new()
	{
		RepositoryFullName = "panoramicdata/Sample",
		Organization = "panoramicdata",
		OpenIssues = [.. issues],
		OpenIssuesKnown = true,
		Assessment = new RepoAssessment
		{
			RepositoryFullName = "panoramicdata/Sample",
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = []
		}
	};

	[Fact]
	public void OneFindingPerDependency_NotPerPullRequest()
	{
		var results = DependabotIssueSynthesizer.Synthesize(Row(
			PullRequest("Bump actions/checkout from 3 to 6", 1),
			PullRequest("Bump actions/checkout from 6 to 7", 2),
			PullRequest("Bump github/codeql-action from 2 to 4", 3)));

		results.Select(r => r.RuleName).Should().BeEquivalentTo([
			"Dependabot: actions/checkout",
			"Dependabot: github/codeql-action"
		]);
	}

	[Fact]
	public void TheRuleIdIsStableAcrossRepositoriesSoTheTreeGroupsThem()
	{
		var one = DependabotIssueSynthesizer
			.Synthesize(Row(PullRequest("Bump actions/checkout from 3 to 6")))
			.Single();

		var two = DependabotIssueSynthesizer
			.Synthesize(Row(PullRequest("Bump Actions/Checkout from 6 to 7")))
			.Single();

		one.RuleId.Should().Be(two.RuleId,
			"the tree groups by rule id, so the same dependency in two repositories must be one node");
		one.RuleId.Should().StartWith(DependabotIssueSynthesizer.RuleIdPrefix);
	}

	[Fact]
	public void FindingsSitInTheDependencyAutomationCategory()
		=> DependabotIssueSynthesizer
			.Synthesize(Row(PullRequest("Bump actions/checkout from 3 to 6")))
			.Should().OnlyContain(r => r.Category == AssessmentCategory.DependencyAutomation);

	[Fact]
	public void FindingsAreFailures_SoTheyAppearAtAll()
		=> DependabotIssueSynthesizer
			.Synthesize(Row(PullRequest("Bump actions/checkout from 3 to 6")))
			.Should().OnlyContain(r => !r.Passed);

	[Fact]
	public void TheMessageNamesThePullRequestsBehindIt()
	{
		var result = DependabotIssueSynthesizer
			.Synthesize(Row(PullRequest("Bump actions/checkout from 3 to 6", 3)))
			.Single();

		result.Message.Should().Contain("#3").And.Contain("3").And.Contain("6");
	}

	[Fact]
	public void AVerdictAlreadyReached_IsCarriedIntoTheMessage()
	{
		var pullRequest = PullRequest("Bump actions/checkout from 3 to 6", 3);
		pullRequest.TriageVerdict = DependabotVerdict.AlreadySatisfied;

		DependabotIssueSynthesizer
			.Synthesize(Row(pullRequest))
			.Single()
			.Message.Should().Contain("supersed", "what triage concluded is worth reading in the tree");
	}

	[Fact]
	public void HumanPullRequestsAndPlainIssues_AreNotFindings()
		=> DependabotIssueSynthesizer
			.Synthesize(Row(
				PullRequest("Bump actions/checkout from 3 to 6", 1, author: "davidbond"),
				PullRequest("Something is broken", 2, isPullRequest: false)))
			.Should().BeEmpty();

	[Fact]
	public void UnparseablePullRequests_AreNotFindings()
		=> DependabotIssueSynthesizer
			.Synthesize(Row(PullRequest("Bump the nuget group with 3 updates")))
			.Should().BeEmpty("triage leaves them alone, so offering to fix them would be a lie");

	[Fact]
	public void ARowWithNoInboxRead_ContributesNothing()
	{
		var row = Row();
		row.OpenIssuesKnown = false;

		DependabotIssueSynthesizer.Synthesize(row).Should().BeEmpty();
	}

	[Fact]
	public void Augment_AddsTheFindingsWithoutMutatingTheStoredAssessment()
	{
		var row = Row(PullRequest("Bump actions/checkout from 3 to 6"));

		var augmented = DependabotIssueSynthesizer.Augment(row);

		augmented.RuleResults.Should().HaveCount(1);
		row.Assessment!.RuleResults.Should().BeEmpty(
			"rendering happens repeatedly, and mutating the cached assessment would add the findings again every time");
	}

	[Fact]
	public void IsSynthetic_RecognisesOnlyTheseFindings()
	{
		var result = DependabotIssueSynthesizer
			.Synthesize(Row(PullRequest("Bump actions/checkout from 3 to 6")))
			.Single();

		DependabotIssueSynthesizer.IsSynthetic(result.RuleId).Should().BeTrue();
		DependabotIssueSynthesizer.IsSynthetic("CI-05").Should().BeFalse();
	}
}
