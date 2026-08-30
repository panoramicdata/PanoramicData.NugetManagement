using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the hosted service that actually drives the refresher. Registering a refresher that nothing
/// resolves leaves the cache frozen at whatever was seeded, so every package the seed did not mention
/// is a permanent miss and the freshness half of the gate never fires for it. No test here touches
/// the network: the lookup is a delegate.
/// </summary>
public class NuGetVersionRefreshServiceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _published = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

	[Fact]
	public void TheSweepShouldCoverBothStoresRatherThanEitherAlone()
	{
		var cache = new NuGetVersionCache(null);
		cache.Update("Codacy.Api", "3.0.43", _published, _published);

		var floors = new NuGetFloorCatalog(null);
		floors.Observe("Octokit", "14.0.0", "panoramicdata/Some.Repo");

		Service(cache, floors, out _).PackageIds
			.Should().BeEquivalentTo(
				["Codacy.Api", "Octokit"],
				"between them the two stores already hold every package id this application has seen");
	}

	[Fact]
	public void APackageObservedByAnAssessmentShouldJoinTheNextSweep()
	{
		// This is what makes the sweep self-sustaining rather than frozen at the seed: assessments
		// observe package references into the floor catalogue, and the sweep picks them up.
		var floors = new NuGetFloorCatalog(null);
		var service = Service(new NuGetVersionCache(null), floors, out _);

		floors.Observe("PanoramicData.Blazor", "10.0.205", "panoramicdata/Newly.Assessed");

		service.PackageIds.Should().Contain("PanoramicData.Blazor");
	}

	[Fact]
	public async Task ASweepShouldRefreshTheUnionOfBothStores()
	{
		var cache = new NuGetVersionCache(null);
		cache.Update("Codacy.Api", "3.0.42", _published, _published);

		var floors = new NuGetFloorCatalog(null);
		floors.Observe("Octokit", "13.0.0", "panoramicdata/Some.Repo");

		var service = Service(cache, floors, out var asked);

		await service.RunSweepAsync(TestContext.Current.CancellationToken);

		asked.Should().BeEquivalentTo(["Codacy.Api", "Octokit"]);
		cache.TryGet("Octokit", out _).Should().BeTrue("a package known only to the floor is still worth refreshing");
	}

	[Fact]
	public async Task AnEmptyEstateShouldNotAskNuGetOrgAnything()
	{
		var service = Service(new NuGetVersionCache(null), new NuGetFloorCatalog(null), out var asked);

		var changed = await service.RunSweepAsync(TestContext.Current.CancellationToken);

		changed.Should().Be(0);
		asked.Should().BeEmpty();
	}

	[Fact]
	public async Task AFailingSweepShouldNotKillTheService()
	{
		var cache = new NuGetVersionCache(null);
		cache.Update("Codacy.Api", "3.0.42", _published, _published);

		var service = new NuGetVersionRefreshService(
			cache,
			new NuGetFloorCatalog(null),
			new NuGetVersionRefresher(
				cache,
				(_, _) => throw new InvalidOperationException("nuget.org is having a day"),
				TimeProvider.System,
				NullLogger<NuGetVersionRefresher>.Instance),
			new FakeTimeProvider(_published),
			CreateLogger<NuGetVersionRefreshService>());

		await service.StartAsync(TestContext.Current.CancellationToken);
		await service.StopAsync(TestContext.Current.CancellationToken);

		// ExecuteAsync completing without faulting is the whole assertion: a sweep that threw out of
		// the loop would stop every subsequent cycle, permanently, on one bad day at nuget.org.
		service.ExecuteTask.Should().NotBeNull();
		service.ExecuteTask!.IsFaulted.Should().BeFalse("a failed sweep must be logged and retried, not fatal");
	}

	[Fact]
	public void TheSweepIntervalShouldBeMeasuredInHoursNotMinutes()
		=> NuGetVersionRefreshService.SweepInterval
			.Should().BeGreaterThanOrEqualTo(
				TimeSpan.FromHours(1),
				"package publication is an event measured in days and the shortest grace period is thirty of them");

	private NuGetVersionRefreshService Service(
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		out List<string> asked)
	{
		var questions = new List<string>();
		asked = questions;

		var refresher = new NuGetVersionRefresher(
			cache,
			(packageId, _) =>
			{
				lock (questions)
				{
					questions.Add(packageId);
				}

				return Task.FromResult<(string, DateTimeOffset)?>(("99.0.0", _published));
			},
			// The real clock, because the refresher's pacing gate waits on it and nothing here
			// advances a fake one; the service's own interval below stays fake so it never ticks.
			TimeProvider.System,
			NullLogger<NuGetVersionRefresher>.Instance);

		return new NuGetVersionRefreshService(
			cache,
			floors,
			refresher,
			new FakeTimeProvider(_published),
			CreateLogger<NuGetVersionRefreshService>());
	}
}
