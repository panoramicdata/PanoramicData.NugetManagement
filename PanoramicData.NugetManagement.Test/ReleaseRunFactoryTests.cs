using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ReleaseRunFactory"/>, which reads a GitHub Actions run's status and
/// conclusion strings into the three states the release rules care about.
/// </summary>
/// <remarks>
/// Kept free of Octokit's own types deliberately: the strings GitHub sends are the contract, a
/// <c>WorkflowRun</c> cannot be constructed in a test, and a conclusion GitHub adds later must not
/// be mistaken for a success by a client library that has not heard of it yet.
/// </remarks>
public class ReleaseRunFactoryTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData("queued", ReleaseRunStatus.Queued)]
	[InlineData("pending", ReleaseRunStatus.Queued)]
	[InlineData("waiting", ReleaseRunStatus.Queued)]
	[InlineData("requested", ReleaseRunStatus.Queued)]
	[InlineData("in_progress", ReleaseRunStatus.InProgress)]
	[InlineData("completed", ReleaseRunStatus.Completed)]
	public void ReadsTheStatus(string status, ReleaseRunStatus expected)
	{
		var run = ReleaseRunFactory.From("2.196.75", 1, status, conclusion: "success", htmlUrl: null,
			startedAt: null, updatedAt: null);

		run.Status.Should().Be(expected);
	}

	[Theory]
	[InlineData("success", ReleaseRunConclusion.Success)]
	[InlineData("failure", ReleaseRunConclusion.Failure)]
	[InlineData("cancelled", ReleaseRunConclusion.Cancelled)]
	[InlineData("timed_out", ReleaseRunConclusion.TimedOut)]
	[InlineData("action_required", ReleaseRunConclusion.Other)]
	[InlineData("startup_failure", ReleaseRunConclusion.Other)]
	[InlineData("some_conclusion_github_adds_in_2027", ReleaseRunConclusion.Other)]
	public void ReadsTheConclusion(string conclusion, ReleaseRunConclusion expected)
	{
		var run = ReleaseRunFactory.From("2.196.75", 1, status: "completed", conclusion, htmlUrl: null,
			startedAt: null, updatedAt: null);

		run.Conclusion.Should().Be(expected);
	}

	[Fact]
	public void TreatsAnUnknownStatusAsStillRunning()
	{
		// A status this does not recognise must not read as completed: CI-13 would then report a
		// release that has not finished as one that failed.
		var run = ReleaseRunFactory.From("2.196.75", 1, status: "something_new", conclusion: null,
			htmlUrl: null, startedAt: null, updatedAt: null);

		run.Status.Should().Be(ReleaseRunStatus.InProgress);
		run.Failed.Should().BeFalse();
		run.InFlight.Should().BeTrue();
	}

	[Fact]
	public void LeavesTheConclusionUnset_WhileTheRunIsStillGoing()
	{
		// GitHub reports a null conclusion for an unfinished run, and an unfinished run that reported
		// "not a success" would be indistinguishable from a failure.
		var run = ReleaseRunFactory.From("2.196.75", 1, status: "in_progress", conclusion: null,
			htmlUrl: null, startedAt: null, updatedAt: null);

		run.Conclusion.Should().BeNull();
		run.Failed.Should().BeFalse();
	}

	[Fact]
	public void TreatsAMissingConclusionOnACompletedRunAsNeitherOutcome()
	{
		var run = ReleaseRunFactory.From("2.196.75", 1, status: "completed", conclusion: null,
			htmlUrl: null, startedAt: null, updatedAt: null);

		run.Conclusion.Should().Be(ReleaseRunConclusion.Other);
		run.Succeeded.Should().BeFalse("a run with no conclusion did not publish anything");
	}

	[Fact]
	public void RecordsWhenACompletedRunFinished_SoTheIndexingGraceCanBeMeasured()
	{
		var updatedAt = new DateTimeOffset(2026, 9, 3, 12, 46, 26, TimeSpan.Zero);

		var run = ReleaseRunFactory.From("2.196.75", 33757069381, status: "completed",
			conclusion: "success", htmlUrl: null,
			startedAt: new DateTimeOffset(2026, 9, 3, 12, 45, 44, TimeSpan.Zero), updatedAt: updatedAt);

		run.CompletedAtUtc.Should().Be(updatedAt);
	}

	[Fact]
	public void LeavesTheCompletionTimeUnset_WhileTheRunIsStillGoing()
	{
		// GitHub's updated_at moves while a run is going. Read as a completion time it would put the
		// run "finished" seconds ago and let CI-11 grant an indexing grace to a release that has not
		// published anything yet.
		var run = ReleaseRunFactory.From("2.196.75", 1, status: "in_progress", conclusion: null,
			htmlUrl: null, startedAt: null,
			updatedAt: new DateTimeOffset(2026, 9, 3, 12, 46, 0, TimeSpan.Zero));

		run.CompletedAtUtc.Should().BeNull();
	}

	[Fact]
	public void KeepsTheTagAndTheRunIdentity()
	{
		var run = ReleaseRunFactory.From("2.196.75", 33757069381, status: "completed",
			conclusion: "failure",
			htmlUrl: "https://github.com/panoramicdata/HaloPsa.Api/actions/runs/33757069381",
			startedAt: null, updatedAt: null);

		run.TagRef.Should().Be("2.196.75");
		run.RunId.Should().Be(33757069381);
		run.HtmlUrl.Should().Be("https://github.com/panoramicdata/HaloPsa.Api/actions/runs/33757069381");
	}
}
