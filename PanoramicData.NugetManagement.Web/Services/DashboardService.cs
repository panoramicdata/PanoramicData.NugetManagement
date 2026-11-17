using System.Globalization;
using Microsoft.Extensions.Options;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Orchestrates package discovery, assessment, remediation, testing, and publishing.
/// </summary>
public partial class DashboardService
{
	private readonly NuGetDiscoveryService _nuget;
	private readonly PublishedVersionRefresher _publishedVersions;
	private readonly DashboardCacheService _cache;
	private readonly LocalRepoService _localRepo;
	private readonly RegressionGuardService _regressionGuard;
	private readonly RuntimeSettingsService _runtimeSettings;
	private readonly NuGetOwnedPackageCatalog _owned;
	private readonly RuleWaiverCatalog _waivers;
	private readonly AppSettings _settings;
	private readonly ICodacyAnalysisStateService _codacyAnalysisState;
	private readonly ILogger<DashboardService> _logger;
	private readonly DeclaredPackageIdCache _packageIdCache;

	/// <summary>
	/// Initializes a new instance with a package id cache that remembers nothing between discoveries,
	/// which is correct but spends GitHub calls on every repository each time.
	/// </summary>
	public DashboardService(DashboardStores stores, DashboardWorkers workers, ILogger<DashboardService> logger)
		: this(stores, workers, logger, new DeclaredPackageIdCache(null))
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="DashboardService"/> class. The package id cache
	/// holds what earlier discoveries learned about which repositories declare one.
	/// </summary>
	public DashboardService(
		DashboardStores stores,
		DashboardWorkers workers,
		ILogger<DashboardService> logger,
		DeclaredPackageIdCache packageIdCache)
	{
		_nuget = workers.Nuget;
		_publishedVersions = workers.PublishedVersions;
		_cache = stores.Cache;
		_localRepo = workers.LocalRepo;
		_regressionGuard = workers.RegressionGuard;
		_runtimeSettings = stores.RuntimeSettings;
		_owned = stores.Owned;
		_waivers = stores.Waivers;
		RemediationRegistry = workers.RemediationRegistry;
		_settings = stores.Settings.Value;
		_codacyAnalysisState = workers.CodacyAnalysisState;
		_logger = logger;
		_packageIdCache = packageIdCache;
	}

	/// <summary>
	/// Refreshes the git status for a row (branch, working tree clean state, and sync status with origin).
	/// </summary>
	public async Task RefreshGitStatusAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null || !row.IsClonedLocally)
		{
			return;
		}

		row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.HasUnpushedCommits = await _localRepo.HasUnpushedCommitsAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.IsSyncedWithOrigin = await _localRepo.IsSyncedWithOriginAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.SyncStatusCheckedAtUtc = DateTimeOffset.UtcNow;
		row.LatestTag = await _localRepo.GetLatestTagAsync(repoIdentity, cancellationToken).ConfigureAwait(false);

		// Both halves of VER-05's comparison move when a sync brings commits in, and this is the one
		// path that has just established the clone matches origin — the state in which that rule has
		// anything to say at all.
		row.NextVersion = await _localRepo.GetNextVersionAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Refreshes only the git facts that can be read without contacting the remote: the current branch,
	/// whether the working tree is clean, and whether anything is committed but not yet pushed.
	/// </summary>
	/// <remarks>
	/// Separate from <see cref="RefreshGitStatusAsync"/> because that one compares against origin, which
	/// costs a fetch — too much to pay every time a repository is clicked. These two are local reads, and
	/// they are the ones that go stale behind the app's back: anything done to a checkout outside the
	/// dashboard leaves the row asserting a branch and a cleanliness that were true when it last acted.
	/// The sync comparison is deliberately left alone rather than invalidated, so what it last established
	/// stays on screen with its age (see <see cref="RepositoryDashboardRow.SyncStatusCheckedAtUtc"/>).
	/// </remarks>
	public async Task RefreshLocalGitStatusAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null || !row.IsClonedLocally)
		{
			return;
		}

		row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.HasUnpushedCommits = await _localRepo.HasUnpushedCommitsAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Reads where Codacy's analysis of a repository stands now, for the header chip.
	/// </summary>
	/// <remarks>
	/// Asked on selection rather than taken from the last assessment, because that is the question a
	/// reader looking at the panel is actually asking. An analysis that started since the last sweep
	/// is invisible to anything captured during it.
	/// <para>
	/// A repository that cannot be asked keeps whatever was last known rather than being reset: an
	/// unreachable Codacy is not evidence that an analysis stopped.
	/// </para>
	/// </remarks>
	public async Task RefreshCodacyAnalysisStateAsync(
		RepositoryDashboardRow row,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(_settings.CodacyApiToken))
		{
			return;
		}

		var parts = row.RepositoryFullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return;
		}

		var state = await _codacyAnalysisState
			.GetStateAsync(
				_settings.CodacyApiToken!,
				parts[0],
				parts[1],
				row.Assessment?.DefaultBranch ?? row.CurrentBranch,
				cancellationToken)
			.ConfigureAwait(false);

		if (state is null)
		{
			return;
		}

		row.CodacyAnalysisState = state;
		row.CodacyStateCheckedAtUtc = state.RetrievedAtUtc;
	}

	/// <summary>
	/// Returns a short preview of dirty working tree lines for diagnostics in UI output.
	/// </summary>
	public async Task<IReadOnlyList<string>> GetWorkingTreeStatusPreviewAsync(
		RepositoryDashboardRow row,
		int maxLines,
		CancellationToken cancellationToken)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null || !row.IsClonedLocally)
		{
			return [];
		}

		return await _localRepo.GetWorkingTreeStatusPreviewAsync(repoIdentity, maxLines, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs tests on a local repository.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="onOutput">Where the test output goes.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <param name="sandbox">
	/// Whether to run without this machine's credentials, as CI does. A model-driven run turns it on: the
	/// tests it triggers should not reach real services with real secrets, or print them.
	/// </param>
	/// <param name="testClasses">Only these classes, or null for the whole suite.</param>
	public async Task RunTestsAsync(
		RepositoryDashboardRow row,
		Action<string>? onOutput,
		CancellationToken cancellationToken,
		bool sandbox,
		IReadOnlyList<string>? testClasses)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.Testing;
		row.StatusMessage = "Running tests...";

		var (success, _) = await _localRepo.RunTestsAsync(repoIdentity, onOutput, cancellationToken, sandbox, testClasses).ConfigureAwait(false);

		row.Status = success ? PackageStatus.TestsPassed : PackageStatus.TestsFailed;
		row.StatusMessage = success ? "All tests passed." : "Tests failed.";
	}

	/// <summary>
	/// Runs the publish script on a local repository.
	/// </summary>
	public async Task RunPublishAsync(
		RepositoryDashboardRow row,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.Publishing;
		row.StatusMessage = "Publishing...";

		var (success, _) = await _localRepo.RunPublishScriptAsync(repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);

		row.Status = success ? PackageStatus.Published : PackageStatus.Error;
		row.StatusMessage = success ? "Published successfully." : "Publish failed.";
	}

	/// <summary>
	/// Checks whether a NuGet package is still listed (not deprecated/de-listed).
	/// </summary>
	public Task<bool> IsPackageListedAsync(string packageId, CancellationToken cancellationToken)
		=> _nuget.IsPackageListedAsync(packageId, cancellationToken);

	internal static Dictionary<AssessmentCategory, CategorySummary> BuildCategorySummaries(List<RuleResult> results)
	{
		var summaries = new Dictionary<AssessmentCategory, CategorySummary>();

		foreach (var group in results.GroupBy(r => r.Category))
		{
			summaries[group.Key] = new CategorySummary
			{
				Passed = group.Count(r => r.Passed && r.Waiver is null),
				Waived = group.Count(r => r.Waiver is not null),
				Criticals = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Critical),
				Errors = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Error),
				Warnings = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Warning),
				Infos = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Info),
			};
		}

		return summaries;
	}

}
