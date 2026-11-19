using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>The state a <see cref="DashboardService"/> reads and records: caches, settings and catalogues.</summary>
/// <param name="Cache">The estate as last discovered.</param>
/// <param name="RuntimeSettings">User settings, chiefly which repositories are excluded.</param>
/// <param name="Owned">The packages the estate publishes itself.</param>
/// <param name="Waivers">The agreed rule waivers.</param>
/// <param name="Settings">The application settings.</param>
public sealed record DashboardStores(
	DashboardCacheService Cache,
	RuntimeSettingsService RuntimeSettings,
	NuGetOwnedPackageCatalog Owned,
	RuleWaiverCatalog Waivers,
	IOptions<AppSettings> Settings);

/// <summary>The services a <see cref="DashboardService"/> calls out to.</summary>
/// <param name="Nuget">Package discovery on nuget.org.</param>
/// <param name="PublishedVersions">Reads what is published now.</param>
/// <param name="LocalRepo">Clones and the git operations performed on them.</param>
/// <param name="RemediationRegistry">Which rules can be fixed automatically.</param>
/// <param name="RegressionGuard">Verifies pushes in the background.</param>
/// <param name="CodacyAnalysisState">Whether Codacy is still analysing a repository.</param>
public sealed record DashboardWorkers(
	NuGetDiscoveryService Nuget,
	PublishedVersionRefresher PublishedVersions,
	LocalRepoService LocalRepo,
	RemediationRegistry RemediationRegistry,
	RegressionGuardService RegressionGuard,
	ICodacyAnalysisStateService CodacyAnalysisState);
