using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotTriageRunner.Preview"/>: what the tree shows about pull requests
/// nobody has acted on yet.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="TriageRestampTests"/>, and deliberately different from it in the one
/// way that matters: a preview has closed nothing, so it drops nothing. A pull request Fix would close
/// is still open until Fix runs, and hiding it would be reporting an action that has not happened.
/// </remarks>
public class TriagePreviewTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryIssue Item(int number, bool isPullRequest = true) => new()
	{
		Number = number,
		Title = $"Item {number}",
		IsPullRequest = isPullRequest,
		HtmlUrl = $"https://github.com/panoramicdata/Athonet.Api/pull/{number}",
		AuthorLogin = "dependabot[bot]",
		CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
	};

	private static DependabotTriage Verdict(RepositoryIssue issue, DependabotVerdict verdict)
		=> new(issue, null, verdict, $"reason for {issue.Number}", null);

	[Fact]
	public void Preview_RecordsEachVerdictAndItsReason()
	{
		var covered = Item(5);

		DependabotTriageRunner.Preview([covered], [Verdict(covered, DependabotVerdict.ValidCovered)]);

		covered.TriageVerdict.Should().Be(DependabotVerdict.ValidCovered);
		covered.TriageReason.Should().Be("reason for 5",
			"the reason names the rule that will move it, which is the whole point of showing a "
				+ "verdict before anybody presses Fix");
	}

	[Fact]
	public void Preview_KeepsShowingWhatFixWouldClose()
	{
		var satisfied = Item(3);
		var obsolete = Item(4);

		DependabotTriageRunner.Preview(
			[satisfied, obsolete],
			[
				Verdict(satisfied, DependabotVerdict.AlreadySatisfied),
				Verdict(obsolete, DependabotVerdict.Obsolete)
			]);

		satisfied.TriageVerdict.Should().Be(DependabotVerdict.AlreadySatisfied,
			"nothing has been closed yet, so the pull request is still open and must still be listed");
		obsolete.TriageVerdict.Should().Be(DependabotVerdict.Obsolete);
	}

	[Fact]
	public void Preview_LeavesItemsTriageSaidNothingAbout()
	{
		var plainIssue = Item(9, isPullRequest: false);

		DependabotTriageRunner.Preview([plainIssue], []);

		plainIssue.TriageVerdict.Should().BeNull("triage reached no verdict on it, and must not imply one");
		plainIssue.TriageReason.Should().BeNull();
	}

	[Fact]
	public void Preview_ReplacesAnEarlierVerdictRatherThanKeepingIt()
	{
		var issue = Item(7);
		issue.TriageVerdict = DependabotVerdict.ValidUncovered;
		issue.TriageReason = "stale reason";

		DependabotTriageRunner.Preview([issue], [Verdict(issue, DependabotVerdict.ValidCovered)]);

		issue.TriageVerdict.Should().Be(DependabotVerdict.ValidCovered,
			"a re-assessment that found a rule now failing has superseded what the last one concluded");
		issue.TriageReason.Should().Be("reason for 7");
	}
}
