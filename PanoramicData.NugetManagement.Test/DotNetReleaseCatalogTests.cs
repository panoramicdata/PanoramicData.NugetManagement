using System.Reflection;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DotNetReleaseCatalog"/>: the standard's .NET version constants are derived
/// from the channel Microsoft currently supports, not from whatever SDKs the machine running this
/// tool happens to have installed.
/// </summary>
public class DotNetReleaseCatalogTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static DotNetReleaseIndex Index(params DotNetReleaseChannel[] channels)
		=> new() { Channels = channels };

	private static DotNetReleaseChannel Channel(string version, string supportPhase)
		=> new()
		{
			ChannelVersion = version,
			LatestSdk = version + ".400",
			SupportPhase = supportPhase,
			ReleaseType = "lts"
		};

	[Fact]
	public void SelectChannel_PicksTheHighestSupportedChannel()
	{
		var selected = DotNetReleaseCatalog.SelectChannel(Index(
			Channel("11.0", "preview"),
			Channel("10.0", "active"),
			Channel("9.0", "eol")));

		selected.Should().NotBeNull();
		selected!.ChannelVersion.Should().Be("10.0");
	}

	[Fact]
	public void SelectChannel_IgnoresPreviewChannels()
	{
		// A preview SDK is not something every repository in the organization can be expected to have.
		var selected = DotNetReleaseCatalog.SelectChannel(Index(
			Channel("11.0", "go-live"),
			Channel("11.0", "preview"),
			Channel("10.0", "active")));

		selected!.ChannelVersion.Should().Be("10.0");
	}

	[Fact]
	public void SelectChannel_AcceptsMaintenanceChannels()
	{
		var selected = DotNetReleaseCatalog.SelectChannel(Index(
			Channel("10.0", "maintenance"),
			Channel("9.0", "eol")));

		selected!.ChannelVersion.Should().Be("10.0");
	}

	[Fact]
	public void SelectChannel_ComparesChannelsNumerically_NotAsText()
	{
		// Ordinal comparison puts "9.0" above "11.0", which would hold the standard a major behind.
		var selected = DotNetReleaseCatalog.SelectChannel(Index(
			Channel("9.0", "active"),
			Channel("11.0", "active")));

		selected!.ChannelVersion.Should().Be("11.0");
	}

	[Fact]
	public void SelectChannel_ReturnsNull_WhenNothingIsSupported()
	{
		var selected = DotNetReleaseCatalog.SelectChannel(Index(
			Channel("11.0", "preview"),
			Channel("9.0", "eol")));

		selected.Should().BeNull();
	}

	[Fact]
	public void Standard_DerivesEveryVersionConstantFromTheChannel()
	{
		// One source of truth: the SDK pin, the target framework and the CI version specifier cannot
		// drift into disagreeing about which .NET this organization is on.
		var standard = new DotNetChannelStandard("10.0");

		standard.SdkPinVersion.Should().Be("10.0.100");
		standard.TargetFramework.Should().Be("net10.0");
		standard.VersionSpecifier.Should().Be("10.0.x");
	}

	[Fact]
	public void Standard_PinsTheFeatureBandFloor_NotTheChannelsNewestSdk()
	{
		// rollForward never rolls down: pinning 10.0.400 stops every machine whose newest band is 3xx
		// from running any dotnet command. The floor plus a band-crossing rollForward means "a .NET 10
		// SDK", which is what the pin is for.
		new DotNetChannelStandard("10.0").SdkPinVersion.Should().EndWith(".100");
	}

	[Fact]
	public void Current_UsesTheFallback_WhenTheIndexWasNeverFetched()
	{
		// An assessment run offline still has to produce results.
		var catalog = new DotNetReleaseCatalog();

		catalog.Current.Should().Be(DotNetReleaseCatalog.Fallback);
	}

	[Fact]
	public void Current_UsesTheFetchedChannel_AfterTheIndexIsApplied()
	{
		var catalog = new DotNetReleaseCatalog();

		catalog.Apply(Index(Channel("11.0", "active"), Channel("10.0", "maintenance")));

		catalog.Current.ChannelVersion.Should().Be("11.0");
		catalog.Current.SdkPinVersion.Should().Be("11.0.100");
	}

	[Fact]
	public void Current_KeepsTheLastGoodValue_WhenAFetchYieldsNothingSupported()
	{
		var catalog = new DotNetReleaseCatalog();
		catalog.Apply(Index(Channel("11.0", "active")));

		catalog.Apply(Index(Channel("11.0", "preview")));

		catalog.Current.ChannelVersion.Should().Be("11.0", "a bad fetch must not downgrade the standard");
	}

	[Fact]
	public void Standards_DeriveFromTheSupportedChannel_NotTheMachinesInstalledSdks()
	{
		// Issue 76: reading `dotnet --list-sdks` made one machine's install list the standard for
		// every repository, and whoever ran the tool decided what everyone else was measured against.
		var members = typeof(Standards)
			.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
			.Select(m => m.Name)
			.ToList();

		members.Should().NotContain("LatestDotNetSdkVersion");
		members.Should().NotContain("DetectLatestSdkVersion");

		var standard = DotNetReleaseCatalog.Default.Current;
		Standards.DotNetSdkPinVersion.Should().Be(standard.SdkPinVersion);
		Standards.LatestTargetFramework.Should().Be(standard.TargetFramework);
		Standards.LatestDotNetVersionSpecifier.Should().Be(standard.VersionSpecifier);
	}

	[Fact]
	public async Task RefreshAsync_AppliesTheFetchedIndex()
	{
		var catalog = new DotNetReleaseCatalog();

		var refreshed = await catalog.RefreshAsync(
			new StubIndexApi(Index(Channel("11.0", "active"))),
			CancellationToken.None);

		refreshed.Should().BeTrue();
		catalog.Current.ChannelVersion.Should().Be("11.0");
	}

	[Fact]
	public async Task RefreshAsync_FallsBack_WhenTheIndexCannotBeFetched()
	{
		// Offline, or the host is down: an assessment run still has to produce results.
		var catalog = new DotNetReleaseCatalog();

		var refreshed = await catalog.RefreshAsync(new StubIndexApi(null), CancellationToken.None);

		refreshed.Should().BeFalse();
		catalog.Current.Should().Be(DotNetReleaseCatalog.Fallback);
	}

	private sealed class StubIndexApi(DotNetReleaseIndex? index) : IDotNetReleaseIndexApi
	{
		public Task<DotNetReleaseIndex> GetReleasesIndexAsync(CancellationToken cancellationToken)
			=> index is null
				? Task.FromException<DotNetReleaseIndex>(new HttpRequestException("no network"))
				: Task.FromResult(index);
	}

	[Fact]
	public void Current_IsFrozenOnceRead()
	{
		// Results must be stable across a single assessment run: a refresh landing mid-run cannot
		// leave two repositories judged against different versions of the standard.
		var catalog = new DotNetReleaseCatalog();
		catalog.Apply(Index(Channel("10.0", "active")));
		_ = catalog.Current;

		catalog.Apply(Index(Channel("11.0", "active")));

		catalog.Current.ChannelVersion.Should().Be("10.0");
	}
}
