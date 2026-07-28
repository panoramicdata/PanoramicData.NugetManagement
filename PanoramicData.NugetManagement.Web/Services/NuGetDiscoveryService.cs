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
	private readonly ILogger<NuGetDiscoveryService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetDiscoveryService"/> class.
	/// </summary>
	public NuGetDiscoveryService(IOptions<AppSettings> settings, ILogger<NuGetDiscoveryService> logger)
	{
		_settings = settings.Value;
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
		var searchResource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);

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

			foreach (var result in batch)
			{
				var repoUrl = ExtractRepositoryUrl(result);
				results.Add(new NuGetPackageInfo
				{
					PackageId = result.Identity.Id,
					LatestVersion = result.Identity.Version.ToNormalizedString(),
					Organization = owner,
					RepositoryUrl = repoUrl,
					RepositoryOwner = ExtractRepoOwner(repoUrl),
					RepositoryName = ExtractRepoName(repoUrl)
				});
			}

			skip += take;

			if (batch.Count < take)
			{
				break;
			}
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

	private static string? ExtractRepositoryUrl(IPackageSearchMetadata metadata)
	{
		var projectUrl = metadata.ProjectUrl?.ToString();
		if (projectUrl is not null && projectUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
		{
			return projectUrl;
		}

		return null;
	}

	private static string? ExtractRepoOwner(string? repoUrl)
	{
		if (repoUrl is null)
		{
			return null;
		}

		// Extract owner from https://github.com/owner/repo
		var uri = new Uri(repoUrl);
		var segments = uri.AbsolutePath.Trim('/').Split('/');
		return segments.Length >= 1 && !string.IsNullOrEmpty(segments[0]) ? segments[0] : null;
	}

	private static string? ExtractRepoName(string? repoUrl)
	{
		if (repoUrl is null)
		{
			return null;
		}

		// Extract repo name from https://github.com/owner/repo, stripping any trailing ".git"
		var uri = new Uri(repoUrl);
		var segments = uri.AbsolutePath.Trim('/').Split('/');
		if (segments.Length < 2)
		{
			return null;
		}

		var name = segments[1];
		return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
			? name[..^4]
			: name;
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
}
