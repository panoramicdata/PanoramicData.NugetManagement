using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the only component permitted to contact nuget.org for dependency versions. No test here
/// touches the network: the lookup is a delegate.
/// </summary>
public class NuGetVersionRefresherTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _published = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task ShouldRecordEveryPackageItLooksUp()
	{
		var cache = new NuGetVersionCache(null);
		var refresher = Refresher(cache, (id, _) => Task.FromResult<(string, DateTimeOffset)?>(("3.0.43", _published)));

		await refresher.RefreshAsync(["Codacy.Api", "Octokit"], TestContext.Current.CancellationToken);

		cache.TryGet("Codacy.Api", out _).Should().BeTrue();
		cache.TryGet("Octokit", out _).Should().BeTrue();
	}

	[Fact]
	public async Task ShouldReportHowManyPackagesActuallyChanged()
	{
		var cache = new NuGetVersionCache(null);
		var refresher = Refresher(cache, (id, _) => Task.FromResult<(string, DateTimeOffset)?>(("3.0.43", _published)));

		await refresher.RefreshAsync(["Codacy.Api"], TestContext.Current.CancellationToken);

		var secondSweep = await refresher.RefreshAsync(["Codacy.Api"], TestContext.Current.CancellationToken);

		secondSweep.Should().Be(0, "an unchanged sweep must not dirty the committed file");
	}

	[Fact]
	public async Task ShouldQueryEachPackageOnlyOnce()
	{
		var calls = new List<string>();
		var cache = new NuGetVersionCache(null);
		var refresher = Refresher(cache, (id, _) =>
		{
			lock (calls)
			{
				calls.Add(id);
			}

			return Task.FromResult<(string, DateTimeOffset)?>(("3.0.43", _published));
		});

		await refresher.RefreshAsync(["Codacy.Api", "codacy.api", "Codacy.Api"], TestContext.Current.CancellationToken);

		calls.Should().ContainSingle("duplicate ids across repositories are the same question");
	}

	[Fact]
	public async Task APackageItCannotReadShouldLeaveTheCacheAsItWas()
	{
		var cache = new NuGetVersionCache(null);
		cache.Update("Codacy.Api", "3.0.42", _published, _published);

		var refresher = Refresher(cache, (_, _) => Task.FromResult<(string, DateTimeOffset)?>(null));
		await refresher.RefreshAsync(["Codacy.Api"], TestContext.Current.CancellationToken);

		cache.TryGet("Codacy.Api", out var snapshot);
		snapshot!.LatestVersion.Should().Be("3.0.42", "a version known a minute ago beats no version at all");
	}

	[Fact]
	public async Task AFailingLookupShouldNotAbandonTheRestOfTheSweep()
	{
		var cache = new NuGetVersionCache(null);
		var refresher = Refresher(cache, (id, _) => id == "Codacy.Api"
			? throw new HttpRequestException("nuget.org is down")
			: Task.FromResult<(string, DateTimeOffset)?>(("1.0.0", _published)));

		await refresher.RefreshAsync(["Codacy.Api", "Octokit"], TestContext.Current.CancellationToken);

		cache.TryGet("Octokit", out _).Should().BeTrue();
	}

	[Fact]
	public async Task TheRequestsPerSecondLimitShouldBeHonoured()
	{
		// Concurrency alone does not bound the request rate: four slots that each complete instantly
		// is an unbounded stream of requests at a service nobody pays us to hammer. At two requests a
		// second the sweep may start one straight away and the next only 500ms later.
		var timeProvider = new FakeTimeProvider(_published);
		var started = 0;
		var refresher = new NuGetVersionRefresher(
			new NuGetVersionCache(null),
			(_, _) =>
			{
				Interlocked.Increment(ref started);
				return Task.FromResult<(string, DateTimeOffset)?>(("1.0.0", _published));
			},
			timeProvider,
			NullLogger<NuGetVersionRefresher>.Instance,
			requestsPerSecond: 2);

		var sweep = refresher.RefreshAsync(["A", "B", "C", "D"], TestContext.Current.CancellationToken);

		await WaitForAsync(() => Volatile.Read(ref started) >= 1).ConfigureAwait(true);
		await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
		Volatile.Read(ref started).Should().Be(1, "the clock has not moved, so no second slot has arrived");

		timeProvider.Advance(TimeSpan.FromMilliseconds(500));
		await WaitForAsync(() => Volatile.Read(ref started) >= 2).ConfigureAwait(true);
		await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
		Volatile.Read(ref started).Should().Be(2, "half a second buys exactly one more request");

		timeProvider.Advance(TimeSpan.FromSeconds(1));
		await sweep.ConfigureAwait(true);

		Volatile.Read(ref started).Should().Be(4, "the whole sweep completes once its slots have arrived");
	}

	private static async Task WaitForAsync(Func<bool> condition)
	{
		for (var attempt = 0; attempt < 500 && !condition(); attempt++)
		{
			await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(false);
		}
	}

	// The pacing gate waits on the refresher's own TimeProvider, so tests that are not about pacing
	// use the real clock: a FakeTimeProvider nobody advances would leave the second request of every
	// sweep waiting forever for its slot. The pacing test below advances a fake clock deliberately.
	private static NuGetVersionRefresher Refresher(
		NuGetVersionCache cache,
		Func<string, CancellationToken, Task<(string, DateTimeOffset)?>> lookup)
		=> new(
			cache,
			lookup,
			TimeProvider.System,
			NullLogger<NuGetVersionRefresher>.Instance);
}
