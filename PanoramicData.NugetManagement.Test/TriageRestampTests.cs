using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotTriageRunner.Restamp"/>: what the tree shows after a triage pass —
/// the verdict on each surviving item, and no trace of the ones just closed.
/// </summary>
public class TriageRestampTests(ITestOutputHelper output) : TestWithOutput(output)
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
	public void Restamp_RecordsEachVerdictAndItsReason()
	{
		var uncovered = Item(5);

		var remaining = DependabotTriageRunner.Restamp(
			[uncovered],
			[Verdict(uncovered, DependabotVerdict.ValidUncovered)]);

		var stamped = remaining.Should().ContainSingle().Subject;
		stamped.TriageVerdict.Should().Be(DependabotVerdict.ValidUncovered);
		stamped.TriageReason.Should().Be("reason for 5");
	}

	[Fact]
	public void Restamp_DropsWhateverWasClosed()
	{
		var closed = Item(3);
		var kept = Item(5);

		var remaining = DependabotTriageRunner.Restamp(
			[closed, kept],
			[
				Verdict(closed, DependabotVerdict.AlreadySatisfied),
				Verdict(kept, DependabotVerdict.ValidUncovered)
			]);

		remaining.Select(i => i.Number).Should().Equal([5],
			"a closed pull request has left the open list, so the tree must stop showing it");
	}

	[Fact]
	public void Restamp_LeavesItemsTriageSaidNothingAbout()
	{
		var plainIssue = Item(9, isPullRequest: false);

		var remaining = DependabotTriageRunner.Restamp([plainIssue], []);

		var kept = remaining.Should().ContainSingle().Subject;
		kept.Number.Should().Be(9);
		kept.TriageVerdict.Should().BeNull("triage reached no verdict on it, and must not imply one");
	}
}
