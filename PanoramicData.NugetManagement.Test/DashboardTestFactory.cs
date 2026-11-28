using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>Builds a <see cref="DashboardService"/> whose collaborators do nothing beyond what a test gives them.</summary>
internal static class DashboardTestFactory
{
	public static DashboardService Create(
		IOptions<AppSettings> appSettings,
		RuntimeSettingsService runtimeSettings,
		LocalRepoService localRepo,
		DashboardCacheService cache)
		=> new(
			new DashboardStores(
				cache,
				runtimeSettings,
				new NuGetOwnedPackageCatalog(null),
				new RuleWaiverCatalog(null),
				appSettings),
			new DashboardWorkers(
				new NuGetDiscoveryService(
					appSettings,
					new NuspecRepositoryResolver(new NoopHttpClientFactory(), NullLogger<NuspecRepositoryResolver>.Instance),
					NullLogger<NuGetDiscoveryService>.Instance),
				new PublishedVersionRefresher(new NoopPublishedVersionSource()),
				localRepo,
				new RemediationRegistry(),
				new NoopCodacyAnalysisStateService()),
			NullLogger<DashboardService>.Instance);
}

/// <summary>Never called for real: the dashboard under test does not touch NuGet.</summary>
internal sealed class NoopHttpClientFactory : IHttpClientFactory
{
	public HttpClient CreateClient(string name) => new();
}

/// <summary>Never called for real: the dashboard under test does not read published versions.</summary>
internal sealed class NoopPublishedVersionSource : IPublishedVersionSource
{
	public Task<string?> GetLatestPublishedVersionAsync(string packageId, CancellationToken cancellationToken)
	{
		FakeArguments.Accept(packageId, cancellationToken);
		return Task.FromResult<string?>(null);
	}
}
