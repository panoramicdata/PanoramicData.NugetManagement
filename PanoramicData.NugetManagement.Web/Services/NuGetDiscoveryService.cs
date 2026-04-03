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
    /// Searches NuGet for all packages owned by the configured organization.
    /// Returns package IDs with their latest versions and repository URLs.
    /// </summary>
    public async Task<List<NuGetPackageInfo>> DiscoverOrganizationPackagesAsync(CancellationToken cancellationToken = default)
    {
        var owner = _settings.NuGetOrganization;
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
                    RepositoryUrl = repoUrl,
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

    private static string? ExtractRepositoryUrl(IPackageSearchMetadata metadata)
    {
        var projectUrl = metadata.ProjectUrl?.ToString();
        if (projectUrl is not null && projectUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return projectUrl;
        }

        return null;
    }

    private static string? ExtractRepoName(string? repoUrl)
    {
        if (repoUrl is null)
        {
            return null;
        }

        // Extract repo name from https://github.com/org/repo
        var uri = new Uri(repoUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 2 ? segments[1] : null;
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
    /// The GitHub repository URL extracted from package metadata.
    /// </summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>
    /// The repository name extracted from the URL.
    /// </summary>
    public string? RepositoryName { get; init; }
}
