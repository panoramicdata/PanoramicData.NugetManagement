using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for dropping packages that are no longer listed on NuGet. The search API answers
/// <c>owner:</c> with unlisted packages included, so PanoramicData.OData.V3.Client and V4.Client —
/// retired, superseded by PanoramicData.OData.Client, and unlisted years ago — kept arriving in the
/// estate and being judged as though somebody still had to do something about them.
/// </summary>
public class UnlistedPackageFilterTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly string[] _packages = ["Meraki.Api", "PanoramicData.OData.V3.Client", "Athonet.Api"];

	[Fact]
	public async Task UnlistedPackagesShouldBeDropped()
	{
		var kept = await NuGetDiscoveryService.KeepListedAsync(
			_packages,
			id => id,
			(id, _) => Task.FromResult(!id.Contains("OData", StringComparison.Ordinal)),
			TestContext.Current.CancellationToken);

		kept.Should().BeEquivalentTo(["Meraki.Api", "Athonet.Api"]);
	}

	[Fact]
	public async Task TheOrderOfWhatSurvivesShouldBeKept()
	{
		var kept = await NuGetDiscoveryService.KeepListedAsync(
			_packages,
			id => id,
			(_, _) => Task.FromResult(true),
			TestContext.Current.CancellationToken);

		kept.Should().ContainInOrder(_packages,
			"the checks run concurrently, so the results must be put back in the order they arrived");
	}

	[Fact]
	public async Task APackageShouldSurviveACheckThatFails()
	{
		var kept = await NuGetDiscoveryService.KeepListedAsync(
			_packages,
			id => id,
			(_, _) => throw new HttpRequestException("nuget.org is unreachable"),
			TestContext.Current.CancellationToken);

		kept.Should().BeEquivalentTo(_packages,
			"an estate must not empty itself because a network call failed");
	}

	[Fact]
	public async Task TheChecksShouldNotAllRunAtOnce()
	{
		var inFlight = 0;
		var highWaterMark = 0;
		var many = Enumerable.Range(0, 200).Select(i => $"Package{i}").ToList();

		var kept = await NuGetDiscoveryService.KeepListedAsync(
			many,
			id => id,
			async (_, token) =>
			{
				highWaterMark = Math.Max(highWaterMark, Interlocked.Increment(ref inFlight));
				await Task.Delay(1, token).ConfigureAwait(false);
				Interlocked.Decrement(ref inFlight);
				return true;
			},
			TestContext.Current.CancellationToken);

		kept.Should().HaveCount(200);
		highWaterMark.Should().BeLessThanOrEqualTo(
			NuGetDiscoveryService.MaxConcurrentListingChecks,
			"nuget.org is a shared resource, not something to open 200 connections to");
	}
}
