using Microsoft.Extensions.Options;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Discovers NuGet packages belonging to the configured organization.
/// </summary>
public class NuGetDiscoveryService
{
	private readonly AppSettings _settings;
	private readonly NuspecRepositoryResolver _resolver;
	private readonly ILogger<NuGetDiscoveryService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetDiscoveryService"/> class.
	/// </summary>
	/// <param name="settings">The application settings.</param>
	/// <param name="resolver">Reads each package's declared repository from its nuspec.</param>
	/// <param name="logger">The logger.</param>
	public NuGetDiscoveryService(
		IOptions<AppSettings> settings,
		NuspecRepositoryResolver resolver,
		ILogger<NuGetDiscoveryService> logger)
	{
		_settings = settings.Value;
		_resolver = resolver;
		_logger = logger;
	}

	/// <summary>
	/// Searches NuGet for all packages owned by the given organization, or by the configured
	/// organization when <paramref name="organization"/> is null or blank.
	/// Returns package IDs with their latest versions and repository URLs.
	/// </summary>
	public async Task<List<NuGetPackageInfo>> DiscoverOrganizationPackagesAsync(
		string? organization = null,
		CancellationToken cancellationToken = default)
	{
		var owner = string.IsNullOrWhiteSpace(organization) ? _settings.NuGetOrganization : organization.Trim();
		_logger.LogInformation("Discovering NuGet packages for owner '{Owner}'...", owner);

		var repository = NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
		// Nullable as of NuGet.Protocol 7.9.0: the source may not offer the resource at all.
		var searchResource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
		if (searchResource is null)
		{
			_logger.LogWarning("NuGet source does not provide package search; cannot discover packages for '{Owner}'.", owner);
			return [];
		}


		var results = new List<NuGetPackageInfo>();
		var skip = 0;
		const int take = 100;

		while (true)
		{
			var searchResults = await searchResource.SearchAsync(
				$"owner:{owner}",
				new SearchFilter(includePrerelease: false),
				skip,
				take,
				NullLogger.Instance,
				cancellationToken).ConfigureAwait(false);

			var batch = searchResults.ToList();
			if (batch.Count == 0)
			{
				break;
			}

			// One small request per package. Sequentially that is a hundred-odd round trips in series;
			// throttled at eight it is a few seconds, and the throttle is what keeps a burst from
			// looking like an attack to the source.
			var resolved = new NuGetPackageInfo[batch.Count];

			await Parallel.ForEachAsync(
				Enumerable.Range(0, batch.Count),
				new ParallelOptions
				{
					MaxDegreeOfParallelism = 8,
					CancellationToken = cancellationToken
				},
				async (index, token) =>
				{
					var result = batch[index];
					var resolution = await _resolver.ResolveAsync(
						result.Identity.Id,
						result.Identity.Version.ToNormalizedString(),
						result.ProjectUrl?.ToString(),
						token).ConfigureAwait(false);

					resolved[index] = new NuGetPackageInfo
					{
						PackageId = result.Identity.Id,
						LatestVersion = result.Identity.Version.ToNormalizedString(),
						Organization = owner,
						RepositoryUrl = resolution.RepositoryUrl,
						RepositoryOwner = GitHubRepositoryUrl.Owner(resolution.RepositoryUrl),
						RepositoryName = GitHubRepositoryUrl.Name(resolution.RepositoryUrl),
						ResolutionOutcome = resolution.Outcome,
						ResolutionError = resolution.Error
					};
				}).ConfigureAwait(false);

			results.AddRange(resolved);

			skip += take;

			if (batch.Count < take)
			{
				break;
			}
		}

		var unresolved = results
			.Where(p => p.ResolutionOutcome is RepositoryResolutionOutcome.LookupFailed)
			.Select(p => p.PackageId)
			.ToList();

		if (unresolved.Count > 0)
		{
			_logger.LogWarning(
				"Could not read the nuspec for {Count} package(s) of '{Owner}': {Packages}. Their repositories are unchanged from the last successful discovery; rediscover to try again.",
				unresolved.Count,
				owner,
				string.Join(", ", unresolved));
		}

		_logger.LogInformation("Found {Count} packages for owner '{Owner}'.", results.Count, owner);
		return [.. results.OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>
	/// Checks whether a package is still listed (not deprecated/de-listed) on NuGet.
	/// </summary>
	public async Task<bool> IsPackageListedAsync(string packageId, CancellationToken cancellationToken = default)
	{
		try
		{
			var repository = NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
			var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
			if (metadataResource is null)
			{
				// Cannot verify, so assume still listed to avoid accidental removal.
				return true;
			}

			var metadata = await metadataResource.GetMetadataAsync(
				packageId,
				includePrerelease: false,
				includeUnlisted: false,
				new SourceCacheContext(),
				NullLogger.Instance,
				cancellationToken).ConfigureAwait(false);

			return metadata.Any();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check NuGet listing status for {PackageId}", packageId);
			// If we can't verify, assume it's still listed to avoid accidental removal
			return true;
		}
	}
}

/// <summary>
/// Information about a NuGet package discovered from the NuGet API.
/// </summary>
public class NuGetPackageInfo
{
	/// <summary>
	/// The NuGet package identifier.
	/// </summary>
	public required string PackageId { get; init; }

	/// <summary>
	/// The latest stable version.
	/// </summary>
	public required string LatestVersion { get; init; }

	/// <summary>
	/// The organisation this package was discovered under (the NuGet owner searched for).
	/// This is deliberately distinct from <see cref="RepositoryOwner"/>: a vendored or forked
	/// package can be owned by one organisation on NuGet while its repository lives under
	/// another (e.g. discovered under "panoramicdata" but hosted at EPPlusSoftware/EPPlus), so
	/// grouping by repository owner would file it under the wrong organisation.
	/// </summary>
	public required string Organization { get; init; }

	/// <summary>
	/// The GitHub repository URL extracted from package metadata.
	/// </summary>
	public string? RepositoryUrl { get; init; }

	/// <summary>
	/// The GitHub repository owner extracted from the URL (may differ from the configured
	/// organization for vendored/forked packages, e.g. EPPlusSoftware/EPPlus).
	/// </summary>
	public string? RepositoryOwner { get; init; }

	/// <summary>
	/// The repository name extracted from the URL (with any trailing ".git" removed).
	/// </summary>
	public string? RepositoryName { get; init; }

	/// <summary>
	/// What came of resolving <see cref="RepositoryUrl"/>. A package with no repository is only
	/// ungoverned for a stated reason when this says the nuspec was actually read.
	/// </summary>
	public RepositoryResolutionOutcome ResolutionOutcome { get; init; } = RepositoryResolutionOutcome.NotDeclared;

	/// <summary>
	/// Why resolution failed, when <see cref="ResolutionOutcome"/> is
	/// <see cref="RepositoryResolutionOutcome.LookupFailed"/>.
	/// </summary>
	public string? ResolutionError { get; init; }
}
