using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// The semantic update level between a current and latest NuGet package version.
/// </summary>
public enum PackageUpdateLevel
{
	/// <summary>
	/// No update is available.
	/// </summary>
	None,

	/// <summary>
	/// Only the build/patch portion changes.
	/// </summary>
	Build,

	/// <summary>
	/// The minor version changes.
	/// </summary>
	Minor,

	/// <summary>
	/// The major version changes.
	/// </summary>
	Major
}

/// <summary>
/// Describes the latest available version for a package relative to its current version.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="CurrentVersion">The current declared version.</param>
/// <param name="LatestVersion">The latest stable version available from NuGet.</param>
/// <param name="UpdateLevel">The semantic update level.</param>
public sealed record PackageVersionStatus(
	string PackageId,
	string CurrentVersion,
	string LatestVersion,
	PackageUpdateLevel UpdateLevel);

/// <summary>
/// Queries the NuGet API to determine the latest stable version of a package.
/// </summary>
public class NuGetVersionChecker
{
	private readonly ILogger<NuGetVersionChecker> _logger;
	private readonly SourceRepository _sourceRepository;

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetVersionChecker"/> class.
	/// </summary>
	public NuGetVersionChecker()
		: this(Microsoft.Extensions.Logging.Abstractions.NullLogger<NuGetVersionChecker>.Instance)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetVersionChecker"/> class.
	/// </summary>
	/// <param name="logger">The logger.</param>
	public NuGetVersionChecker(ILogger<NuGetVersionChecker> logger)
	{
		_logger = logger;
		_sourceRepository = NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
	}

	/// <summary>
	/// Gets the latest stable version of a NuGet package.
	/// </summary>
	/// <param name="packageId">The NuGet package ID.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The latest stable version string, or null if not found.</returns>
	public async Task<string?> GetLatestStableVersionAsync(string packageId, CancellationToken cancellationToken = default)
	{
		try
		{
			var metadataResource = await _sourceRepository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
			var metadata = await metadataResource.GetMetadataAsync(
				packageId,
				includePrerelease: false,
				includeUnlisted: false,
				new SourceCacheContext(),
				NuGet.Common.NullLogger.Instance,
				cancellationToken).ConfigureAwait(false);

			var latest = metadata
				.OrderByDescending(m => m.Identity.Version)
				.FirstOrDefault();

			return latest?.Identity.Version.ToNormalizedString();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to query NuGet for package {PackageId}", packageId);
			return null;
		}
	}

	/// <summary>
	/// Checks whether the specified version string matches the latest stable version.
	/// </summary>
	/// <param name="packageId">The NuGet package ID.</param>
	/// <param name="currentVersion">The current version string.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>A tuple of (IsLatest, LatestVersion).</returns>
	public async Task<(bool IsLatest, string? LatestVersion)> IsLatestVersionAsync(
		string packageId,
		string currentVersion,
		CancellationToken cancellationToken = default)
	{
		var latestVersion = await GetLatestStableVersionAsync(packageId, cancellationToken).ConfigureAwait(false);
		if (latestVersion is null)
		{
			return (true, null); // Assume latest if we can't check
		}

		return (string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase), latestVersion);
	}

	/// <summary>
	/// Gets the latest stable version and semantic update level for a package.
	/// </summary>
	/// <param name="packageId">The NuGet package ID.</param>
	/// <param name="currentVersion">The current declared version.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The version status, or null if the package cannot be evaluated.</returns>
	public async Task<PackageVersionStatus?> GetVersionStatusAsync(
		string packageId,
		string currentVersion,
		CancellationToken cancellationToken = default)
	{
		if (!NuGetVersion.TryParse(currentVersion, out var current))
		{
			_logger.LogDebug("Skipping package {PackageId} because version '{Version}' could not be parsed.", packageId, currentVersion);
			return null;
		}

		var latestVersion = await GetLatestStableVersionAsync(packageId, cancellationToken).ConfigureAwait(false);
		if (latestVersion is null || !NuGetVersion.TryParse(latestVersion, out var latest) || latest <= current)
		{
			return null;
		}

		return new PackageVersionStatus(
			packageId,
			currentVersion,
			latestVersion,
			ClassifyUpdateLevel(current, latest));
	}

	internal static PackageUpdateLevel ClassifyUpdateLevel(NuGetVersion current, NuGetVersion latest)
	{
		if (latest <= current)
		{
			return PackageUpdateLevel.None;
		}

		if (latest.Major != current.Major)
		{
			return PackageUpdateLevel.Major;
		}

		if (latest.Minor != current.Minor)
		{
			return PackageUpdateLevel.Minor;
		}

		return PackageUpdateLevel.Build;
	}
}
