using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for discarding a dashboard cache written by an older understanding of what is governed.
/// Rules change, and rows the previous rules produced outlive them: the cache that governed
/// rimland/EPPlus went on driving the screen after the fix landed, because nothing recorded which
/// version of the rules had produced it.
/// </summary>
public class DashboardCacheVersionTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _localAppData = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void ACacheFromAnOlderDiscoveryVersionShouldBeDiscarded()
	{
		WriteCache(DashboardCacheService.DiscoveryVersion - 1);

		CreateService().GetCachedRows()
			.Should().BeNull("rows produced by rules that no longer apply must not reach the screen");
	}

	[Fact]
	public void ACacheFromTheCurrentDiscoveryVersionShouldBeLoaded()
	{
		WriteCache(DashboardCacheService.DiscoveryVersion);

		CreateService().GetCachedRows()
			.Should().ContainSingle().Which.PackageId.Should().Be("Meraki.Api");
	}

	[Fact]
	public void ACacheWrittenBeforeVersioningShouldBeDiscarded()
	{
		// Files already on disk carry no version at all, so they deserialize as zero.
		WriteCacheJson("""{"lastRefreshUtc":"2026-08-29T10:56:25+00:00","rows":[{"packageId":"Meraki.Api"}]}""");

		CreateService().GetCachedRows().Should().BeNull();
	}

	private void WriteCache(int discoveryVersion)
		=> WriteCacheJson(JsonSerializer.Serialize(new
		{
			discoveryVersion,
			lastRefreshUtc = DateTimeOffset.UtcNow,
			rows = new[] { new { packageId = "Meraki.Api", organization = "panoramicdata" } }
		}));

	private void WriteCacheJson(string json)
	{
		Directory.CreateDirectory(_localAppData);
		File.WriteAllText(CachePath, json);
	}

	private string CachePath => Path.Combine(_localAppData, "dashboard-cache.json");

	// A cache file of this test's own, so the developer's real cache is neither read nor written.
	private DashboardCacheService CreateService()
		=> new(NullLogger<DashboardCacheService>.Instance, CachePath);

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_localAppData))
			{
				Directory.Delete(_localAppData, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
