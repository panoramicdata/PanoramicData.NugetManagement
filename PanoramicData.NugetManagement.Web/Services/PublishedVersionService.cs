using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Reads the newest version of a package actually present on nuget.org.
/// </summary>
public interface IPublishedVersionSource
{
	/// <summary>
	/// Returns the highest stable version published for the package, or null when nuget.org knows no
	/// stable version of it.
	/// </summary>
	/// <param name="packageId">The NuGet package identifier.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<string?> GetLatestPublishedVersionAsync(string packageId, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IPublishedVersionSource"/>, reading nuget.org's package version index.
/// </summary>
/// <remarks>
/// This deliberately uses the version index (the "flat container") rather than the search API that
/// package discovery uses. Search is a built index and lags a push by minutes to hours: an hour
/// after 1.0.49 of Athonet.Api was published, search still answered 1.0.47 while the version index
/// already listed 1.0.49. For "did this tag actually reach nuget.org?" only the latter is an answer.
/// </remarks>
public class PublishedVersionService : IPublishedVersionSource
{
	private readonly ILogger<PublishedVersionService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="PublishedVersionService"/> class.
	/// </summary>
	/// <param name="logger">The logger.</param>
	public PublishedVersionService(ILogger<PublishedVersionService> logger)
	{
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<string?> GetLatestPublishedVersionAsync(string packageId, CancellationToken cancellationToken)
	{
		var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
		var resource = await repository
			.GetResourceAsync<FindPackageByIdResource>(cancellationToken)
			.ConfigureAwait(false);

		if (resource is null)
		{
			_logger.LogWarning("NuGet source does not provide package version lookup; cannot read the published version of {PackageId}.", packageId);
			return null;
		}

		// NuGet.Protocol caches the version index on disk, and its whole purpose here is to answer
		// "has the release that just happened landed?". A cached answer would make Re-assess unable to
		// change its mind however many times it was pressed, which is the fault this exists to fix.
		using var cache = new SourceCacheContext { NoCache = true, DirectDownload = true };

		var versions = await resource
			.GetAllVersionsAsync(packageId, cache, NullLogger.Instance, cancellationToken)
			.ConfigureAwait(false);

		// Prerelease versions are not what a release tag is compared against, and discovery excludes
		// them too, so the two views of a package agree on what "published" means.
		var latest = versions
			.Where(version => !version.IsPrerelease)
			.OrderByDescending(version => version, VersionComparer.VersionRelease)
			.FirstOrDefault();

		return latest?.ToNormalizedString();
	}
}
