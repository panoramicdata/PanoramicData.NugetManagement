using System.Text.Json.Serialization;
using Refit;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// One channel entry from Microsoft's .NET release index.
/// </summary>
public sealed record DotNetReleaseChannel
{
	/// <summary>The major.minor channel, for example <c>10.0</c>.</summary>
	[JsonPropertyName("channel-version")]
	public required string ChannelVersion { get; init; }

	/// <summary>The newest SDK published on this channel, for example <c>10.0.400</c>.</summary>
	[JsonPropertyName("latest-sdk")]
	public required string LatestSdk { get; init; }

	/// <summary>The support phase: <c>preview</c>, <c>go-live</c>, <c>active</c>, <c>maintenance</c> or <c>eol</c>.</summary>
	[JsonPropertyName("support-phase")]
	public required string SupportPhase { get; init; }

	/// <summary>The release type, <c>lts</c> or <c>sts</c>.</summary>
	[JsonPropertyName("release-type")]
	public string? ReleaseType { get; init; }
}

/// <summary>
/// Microsoft's .NET release index: every channel and its current state.
/// </summary>
public sealed record DotNetReleaseIndex
{
	/// <summary>The channels, newest first as published.</summary>
	[JsonPropertyName("releases-index")]
	public required IReadOnlyList<DotNetReleaseChannel> Channels { get; init; }
}

/// <summary>
/// The .NET version standard derived from a single supported channel. Every version constant the
/// rules assess against comes from here, so the SDK pin, the target framework and the CI version
/// specifier cannot drift into disagreeing about which .NET the organization is on.
/// </summary>
/// <param name="ChannelVersion">The major.minor channel, for example <c>10.0</c>.</param>
public sealed record DotNetChannelStandard(string ChannelVersion)
{
	/// <summary>
	/// The SDK version to pin in <c>global.json</c>: the feature-band floor for the channel, for
	/// example <c>10.0.100</c>.
	/// </summary>
	/// <remarks>
	/// Deliberately the floor and not the channel's newest SDK. <c>rollForward</c> never rolls down,
	/// so pinning <c>10.0.400</c> stops every machine whose newest band is 3xx from running any
	/// dotnet command. Security patches are no argument for a higher pin either: Microsoft services
	/// every live feature band in the same release, so <c>10.0.111</c> and <c>10.0.400</c> carry the
	/// same fixes. The floor plus a band-crossing <c>rollForward</c> means "a .NET 10 SDK", which is
	/// what the pin is actually for.
	/// </remarks>
	public string SdkPinVersion { get; } = ChannelVersion + ".100";

	/// <summary>The target framework moniker for this channel, for example <c>net10.0</c>.</summary>
	public string TargetFramework { get; } = "net" + ChannelVersion;

	/// <summary>The CI version specifier for this channel, for example <c>10.0.x</c>.</summary>
	public string VersionSpecifier { get; } = ChannelVersion + ".x";
}

/// <summary>
/// Microsoft's .NET release index, read over HTTP.
/// </summary>
public interface IDotNetReleaseIndexApi
{
	/// <summary>Fetches the release index listing every .NET channel and its support phase.</summary>
	/// <param name="cancellationToken">A cancellation token.</param>
	[Get("/dotnet/release-metadata/releases-index.json")]
	Task<DotNetReleaseIndex> GetReleasesIndexAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The .NET version standard the rules assess against, taken from the newest channel Microsoft
/// still supports.
/// </summary>
/// <remarks>
/// <para>
/// This replaced reading <c>dotnet --list-sdks</c>. That made one machine's install list the
/// standard for every repository in the organization: whoever ran the tool decided what everyone
/// else was measured against, and a machine a release behind quietly held the whole organization
/// back.
/// </para>
/// <para>
/// Like <see cref="ActionVersionCatalog"/>, the value used within a single process is frozen at
/// first read, so an assessment run cannot judge two repositories against different standards.
/// </para>
/// </remarks>
public sealed class DotNetReleaseCatalog
{
	private readonly Lock _lock = new();
	private DotNetChannelStandard? _latest;
	private DotNetChannelStandard? _frozen;

	/// <summary>The ambient catalog the rules read from.</summary>
	public static DotNetReleaseCatalog Default { get; } = new();

	/// <summary>
	/// The standard used when the release index cannot be reached and nothing better has been
	/// fetched. An assessment run offline still has to produce results.
	/// </summary>
	public static DotNetChannelStandard Fallback { get; } = new("10.0");

	/// <summary>The base address of the release-metadata host.</summary>
	public static Uri BaseAddress { get; } = new("https://builds.dotnet.microsoft.com");

	/// <summary>
	/// The support phases whose channels the organization can be expected to be on. Preview and
	/// go-live are excluded: a preview SDK is not something every machine will have installed.
	/// </summary>
	private static readonly string[] _supportedPhases = ["active", "maintenance"];

	/// <summary>
	/// The current standard, frozen at first read for the lifetime of the process.
	/// </summary>
	public DotNetChannelStandard Current
	{
		get
		{
			lock (_lock)
			{
				return _frozen ??= _latest ?? Fallback;
			}
		}
	}

	/// <summary>
	/// Selects the channel the standard should follow: the highest-numbered channel Microsoft still
	/// supports, or null if the index lists none.
	/// </summary>
	/// <param name="index">The release index.</param>
	public static DotNetReleaseChannel? SelectChannel(DotNetReleaseIndex index)
		=> index.Channels
			.Where(c => _supportedPhases.Contains(c.SupportPhase, StringComparer.OrdinalIgnoreCase))
			.Select(c => (Channel: c, Parsed: Version.TryParse(c.ChannelVersion, out var v) ? v : null))
			.Where(x => x.Parsed is not null)
			.OrderByDescending(x => x.Parsed)
			.Select(x => x.Channel)
			.FirstOrDefault();

	/// <summary>
	/// Applies a freshly-fetched index. An index listing no supported channel leaves the last good
	/// value in place rather than downgrading the standard.
	/// </summary>
	/// <param name="index">The release index.</param>
	public void Apply(DotNetReleaseIndex index)
	{
		var channel = SelectChannel(index);
		if (channel is null)
		{
			return;
		}

		lock (_lock)
		{
			_latest = new DotNetChannelStandard(channel.ChannelVersion);
		}
	}


	/// <summary>
	/// Fetches the release index and applies it. Never throws: a failed fetch keeps the last good
	/// value, or the fallback if there is none.
	/// </summary>
	/// <param name="api">The release index API.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>True if the index was fetched and applied.</returns>
	public async Task<bool> RefreshAsync(IDotNetReleaseIndexApi api, CancellationToken cancellationToken)
	{
		try
		{
			var index = await api.GetReleasesIndexAsync(cancellationToken).ConfigureAwait(false);
			Apply(index);
			return true;
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested)
		{
			return false;
		}
	}
}
