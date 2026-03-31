using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace PanoramicData.NugetManagement.Services;

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
				NullLogger.Instance,
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
}
