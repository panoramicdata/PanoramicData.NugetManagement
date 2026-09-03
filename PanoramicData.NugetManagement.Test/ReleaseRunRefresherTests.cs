using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ReleaseRunRefresher"/>, which reads the workflow run for a row's newest tag
/// at assessment time so CI-11 and CI-13 can tell an in-flight release from a failed one.
/// </summary>
public class ReleaseRunRefresherTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task RecordsTheRun_ForTheNewestTag()
	{
		var row = Row("2.196.75");

		await Refresher(Completed(ReleaseRunConclusion.Success)).RefreshAsync(row, TestContext.Current.CancellationToken);

		row.ReleaseRun.Should().NotBeNull();
		row.ReleaseRun!.Conclusion.Should().Be(ReleaseRunConclusion.Success);
	}

	[Fact]
	public async Task LeavesTheRunUnknown_WhenTheLookupFails()
	{
		// An unreachable GitHub must not read as "the release run failed", nor as "the release
		// succeeded": both rules treat an unknown run as no evidence either way.
		var row = Row("2.196.75");

		await new ReleaseRunRefresher(new ThrowingReleaseRunSource())
			.RefreshAsync(row, TestContext.Current.CancellationToken);

		row.ReleaseRun.Should().BeNull();
	}

	[Fact]
	public async Task LeavesTheRunUnknown_WhenNoTagIsKnown()
	{
		var row = Row(latestTag: null);

		await Refresher(Completed(ReleaseRunConclusion.Success)).RefreshAsync(row, TestContext.Current.CancellationToken);

		row.ReleaseRun.Should().BeNull("with no tag there is no run to look for");
	}

	[Fact]
	public async Task ClearsAPreviousRun_WhenTheNewestTagNoLongerHasOne()
	{
		// A stale run from the last assessment would let CI-13 keep reporting a failure that is no
		// longer the newest tag's.
		var row = Row("2.196.75");
		row.ReleaseRun = Completed(ReleaseRunConclusion.Failure);

		await Refresher(null).RefreshAsync(row, TestContext.Current.CancellationToken);

		row.ReleaseRun.Should().BeNull();
	}

	private static ReleaseRunRefresher Refresher(ReleaseRun? run) => new(new StubReleaseRunSource(run));

	private static ReleaseRun Completed(ReleaseRunConclusion conclusion) => new()
	{
		TagRef = "2.196.75",
		RunId = 33757069381,
		Status = ReleaseRunStatus.Completed,
		Conclusion = conclusion,
		HtmlUrl = "https://github.com/panoramicdata/HaloPsa.Api/actions/runs/33757069381",
		StartedAtUtc = new DateTimeOffset(2026, 9, 3, 12, 45, 44, TimeSpan.Zero),
		CompletedAtUtc = new DateTimeOffset(2026, 9, 3, 12, 46, 26, TimeSpan.Zero)
	};

	private static RepositoryDashboardRow Row(string? latestTag) => new()
	{
		RepositoryFullName = "panoramicdata/HaloPsa.Api",
		LatestTag = latestTag
	};

	private sealed class StubReleaseRunSource(ReleaseRun? run) : IReleaseRunSource
	{
		public Task<ReleaseRun?> GetReleaseRunAsync(
			string repositoryFullName,
			string tag,
			CancellationToken cancellationToken) => Task.FromResult(run);
	}

	private sealed class ThrowingReleaseRunSource : IReleaseRunSource
	{
		public Task<ReleaseRun?> GetReleaseRunAsync(
			string repositoryFullName,
			string tag,
			CancellationToken cancellationToken) => throw new InvalidOperationException("GitHub is unreachable");
	}
}
