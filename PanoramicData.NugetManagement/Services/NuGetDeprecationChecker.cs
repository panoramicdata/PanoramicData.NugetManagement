using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Describes a package version that nuget.org marks as deprecated.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Reasons">The deprecation reasons nuget.org reports, e.g. "Legacy", "CriticalBugs".</param>
/// <param name="Message">The owner's deprecation message, when they supplied one.</param>
/// <param name="AlternatePackageId">The package nuget.org suggests instead, when one is named.</param>
public sealed record PackageDeprecationStatus(
	string PackageId,
	IReadOnlyList<string> Reasons,
	string? Message,
	string? AlternatePackageId);

/// <summary>
/// Queries nuget.org for package deprecation metadata.
/// </summary>
/// <remarks>
/// Reads the same registration blobs the NuGet client itself reads, so what this reports is what a
/// consumer's restore would warn about. Those blobs lag the nuget.org gallery database by up to a
/// few hours, so a package deprecated moments ago will still look healthy here — acceptable for
/// governance, which runs on a schedule rather than in the moment.
/// </remarks>
public class NuGetDeprecationChecker
{
	private readonly ILogger<NuGetDeprecationChecker> _logger;
	private readonly SourceRepository _sourceRepository;
	private readonly Dictionary<string, IReadOnlyList<IPackageSearchMetadata>?> _metadataCache
		= new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetDeprecationChecker"/> class.
	/// </summary>
	public NuGetDeprecationChecker()
		: this(NullLogger<NuGetDeprecationChecker>.Instance)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetDeprecationChecker"/> class.
	/// </summary>
	/// <param name="logger">The logger.</param>
	public NuGetDeprecationChecker(ILogger<NuGetDeprecationChecker> logger)
	{
		_logger = logger;
		_sourceRepository = NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
	}

	/// <summary>
	/// Gets the deprecation status of a package version.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	/// <param name="version">
	/// The version to check, or null to check the latest published version — which is what tells you
	/// whether the package as a whole is deprecated.
	/// </param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>
	/// The deprecation status, or null when the version is not deprecated, the package is unknown, or
	/// nuget.org could not be reached. Returning null on failure keeps a NuGet outage from being
	/// reported as a repository defect.
	/// </returns>
	public async Task<PackageDeprecationStatus?> GetDeprecationAsync(
		string packageId,
		string? version,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(packageId))
		{
			return null;
		}

		var allMetadata = await GetMetadataAsync(packageId, cancellationToken).ConfigureAwait(false);
		if (allMetadata is null || allMetadata.Count == 0)
		{
			return null;
		}

		var match = SelectVersion(allMetadata, version);
		if (match is null)
		{
			return null;
		}

		var deprecation = await match.GetDeprecationMetadataAsync().ConfigureAwait(false);
		if (deprecation is null)
		{
			return null;
		}

		return new PackageDeprecationStatus(
			packageId,
			[.. deprecation.Reasons ?? []],
			string.IsNullOrWhiteSpace(deprecation.Message) ? null : deprecation.Message,
			deprecation.AlternatePackage?.PackageId);
	}

	/// <summary>
	/// Picks the metadata entry to inspect: the requested version when it is published, otherwise the
	/// latest published version.
	/// </summary>
	private static IPackageSearchMetadata? SelectVersion(
		IReadOnlyList<IPackageSearchMetadata> allMetadata,
		string? version)
	{
		if (!string.IsNullOrWhiteSpace(version) && NuGetVersion.TryParse(version, out var requested))
		{
			var exact = allMetadata.FirstOrDefault(m => m.Identity.Version == requested);
			if (exact is not null)
			{
				return exact;
			}
		}

		return allMetadata.MaxBy(m => m.Identity.Version);
	}

	/// <summary>
	/// Fetches every published version's metadata for a package, caching per instance so a rule that
	/// checks the same package across several projects pays for one round trip rather than several.
	/// </summary>
	private async Task<IReadOnlyList<IPackageSearchMetadata>?> GetMetadataAsync(
		string packageId,
		CancellationToken cancellationToken)
	{
		if (_metadataCache.TryGetValue(packageId, out var cached))
		{
			return cached;
		}

		IReadOnlyList<IPackageSearchMetadata>? metadata = null;
		try
		{
			var resource = await _sourceRepository
				.GetResourceAsync<PackageMetadataResource>(cancellationToken)
				.ConfigureAwait(false);

			if (resource is null)
			{
				_logger.LogWarning("NuGet source does not provide package metadata; cannot check {PackageId}", packageId);
			}
			else
			{
				metadata = [.. await resource
					.GetMetadataAsync(
						packageId,
						includePrerelease: true,
						includeUnlisted: true,
						new NuGet.Protocol.Core.Types.SourceCacheContext(),
						NuGet.Common.NullLogger.Instance,
						cancellationToken)
					.ConfigureAwait(false)];
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogWarning(ex, "Failed to query NuGet for deprecation of package {PackageId}", packageId);
		}

		_metadataCache[packageId] = metadata;
		return metadata;
	}
}
