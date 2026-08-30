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
		snapshot.LatestVersion.Should().Be("3.0.42", "a version known a minute ago beats no version at all");
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

	private static NuGetVersionRefresher Refresher(
		NuGetVersionCache cache,
		Func<string, CancellationToken, Task<(string, DateTimeOffset)?>> lookup)
		=> new(
			cache,
			lookup,
			new FakeTimeProvider(_published),
			NullLogger<NuGetVersionRefresher>.Instance);
}
