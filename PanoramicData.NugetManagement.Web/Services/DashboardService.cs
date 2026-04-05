using Microsoft.Extensions.Options;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Orchestrates package discovery, assessment, remediation, testing, and publishing.
/// </summary>
public class DashboardService
{
	private readonly NuGetDiscoveryService _nuget;
	private readonly LocalRepoService _localRepo;
	private readonly AppSettings _settings;
	private readonly ILogger<DashboardService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="DashboardService"/> class.
	/// </summary>
	public DashboardService(
		NuGetDiscoveryService nuget,
		LocalRepoService localRepo,
		RemediationRegistry remediationRegistry,
		IOptions<AppSettings> settings,
		ILogger<DashboardService> logger)
	{
		_nuget = nuget;
		_localRepo = localRepo;
		RemediationRegistry = remediationRegistry;
		_settings = settings.Value;
		_logger = logger;
	}

	/// <summary>
	/// Discovers all packages and builds initial dashboard rows.
	/// </summary>
	public async Task<List<PackageDashboardRow>> DiscoverPackagesAsync(CancellationToken cancellationToken = default)
	{
		var packages = await _nuget.DiscoverOrganizationPackagesAsync(cancellationToken).ConfigureAwait(false);
		var rows = new List<PackageDashboardRow>();

		foreach (var pkg in packages)
		{
			var repoName = pkg.RepositoryName;
			var isCloned = repoName is not null && _localRepo.IsClonedLocally(repoName);

			var row = new PackageDashboardRow
			{
				PackageId = pkg.PackageId,
				LatestVersion = pkg.LatestVersion,
				RepositoryFullName = repoName is not null ? $"{_settings.GitHubOrganization}/{repoName}" : null,
				RepositoryUrl = pkg.RepositoryUrl,
				IsClonedLocally = isCloned,
				LocalPath = repoName is not null ? _localRepo.GetLocalPath(repoName) : null,
				SlnxPath = isCloned && repoName is not null ? _localRepo.FindSlnxFile(repoName) : null,
				Status = isCloned ? PackageStatus.NotAssessed : PackageStatus.NotCloned
			};

			if (isCloned && repoName is not null)
			{
				row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoName, cancellationToken).ConfigureAwait(false);
				row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoName, cancellationToken).ConfigureAwait(false);
			}

			rows.Add(row);
		}

		return rows;
	}

	/// <summary>
	/// Clones a repository locally.
	/// </summary>
	public async Task CloneRepositoryAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		if (row.RepositoryUrl is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "No repository URL available.";
			return;
		}

		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name from URL.";
			return;
		}

		row.Status = PackageStatus.Cloning;
		row.StatusMessage = "Cloning...";

		var cloneUrl = $"https://github.com/{_settings.GitHubOrganization}/{repoName}.git";
		var (success, output) = await _localRepo.CloneAsync(cloneUrl, repoName, onOutput, cancellationToken).ConfigureAwait(false);

		if (success)
		{
			row.IsClonedLocally = true;
			row.LocalPath = _localRepo.GetLocalPath(repoName);
			row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoName, cancellationToken).ConfigureAwait(false);
			row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoName, cancellationToken).ConfigureAwait(false);
			row.Status = PackageStatus.NotAssessed;
			row.StatusMessage = "Cloned successfully.";
		}
		else
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = $"Clone failed: {output}";
		}
	}

	/// <summary>
	/// Assesses a single repository against all governance rules using GitHub API.
	/// </summary>
	public async Task AssessRepositoryAsync(
		PackageDashboardRow row,
		IGitHubClient github,
		CancellationToken cancellationToken = default)
	{
		if (row.RepositoryFullName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "No repository identified.";
			return;
		}

		row.Status = PackageStatus.Assessing;
		row.StatusMessage = "Assessing...";

		try
		{
			var parts = row.RepositoryFullName.Split('/');
			if (parts.Length != 2)
			{
				row.Status = PackageStatus.Error;
				row.StatusMessage = "Invalid repository full name.";
				return;
			}

			var repo = await github.Repository.Get(parts[0], parts[1]).ConfigureAwait(false);
			var repoOptions = new RepoOptions
			{
				ExpectedLicense = _settings.ExpectedLicense,
				ExpectedCopyrightHolder = _settings.CopyrightHolder,
				NuGetUser = _settings.NuGetUser,
			};

			if (!string.IsNullOrEmpty(_settings.CodacyApiToken))
			{
				repoOptions.Codacy = new CodacyOptions
				{
					ApiToken = _settings.CodacyApiToken
				};
			}

			using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
			using var contextBuilder = new RepositoryContextBuilder(github, loggerFactory.CreateLogger<RepositoryContextBuilder>());
			var context = await contextBuilder.BuildAsync(repo, repoOptions, cancellationToken).ConfigureAwait(false);

			var rules = RuleRegistry.Rules;
			var results = new List<RuleResult>();

			foreach (var rule in rules)
			{
				if (repoOptions.SuppressedRules.Contains(rule.RuleId))
				{
					continue;
				}

				var result = await rule.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
				results.Add(result);
			}

			row.Assessment = new RepoAssessment
			{
				RepositoryFullName = row.RepositoryFullName,
				DefaultBranch = context.DefaultBranch,
				AssessedAtUtc = DateTimeOffset.UtcNow,
				RuleResults = results
			};

			// Build category summaries
			row.CategorySummaries = BuildCategorySummaries(results);
			row.Status = PackageStatus.Assessed;
			row.StatusMessage = $"{row.TotalFailures} issue(s) found.";
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to assess {Repo}", row.RepositoryFullName);
			row.Status = PackageStatus.Error;
			row.StatusMessage = $"Assessment failed: {ex.Message}";
		}
	}

	/// <summary>
	/// Assesses a single repository against all governance rules using the local filesystem.
	/// This reads files directly from disk so that changes made by remediations are
	/// immediately visible without pushing to GitHub first.
	/// </summary>
	public async Task AssessLocalRepositoryAsync(
		PackageDashboardRow row,
		CancellationToken cancellationToken = default)
	{
		if (row.RepositoryFullName is null || row.LocalPath is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "No repository or local path identified.";
			return;
		}

		row.Status = PackageStatus.Assessing;
		row.StatusMessage = "Assessing (local)...";

		try
		{
			var repoOptions = new RepoOptions
			{
				ExpectedLicense = _settings.ExpectedLicense,
				ExpectedCopyrightHolder = _settings.CopyrightHolder,
				NuGetUser = _settings.NuGetUser,
			};

			if (!string.IsNullOrEmpty(_settings.CodacyApiToken))
			{
				repoOptions.Codacy = new CodacyOptions
				{
					ApiToken = _settings.CodacyApiToken
				};
			}

			using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
			var localBuilder = new LocalRepositoryContextBuilder(loggerFactory.CreateLogger<LocalRepositoryContextBuilder>());
			var context = localBuilder.Build(row.LocalPath, row.RepositoryFullName, repoOptions);

			var rules = RuleRegistry.Rules;
			var results = new List<RuleResult>();

			foreach (var rule in rules)
			{
				if (repoOptions.SuppressedRules.Contains(rule.RuleId))
				{
					continue;
				}

				var result = await rule.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
				results.Add(result);
			}

			row.Assessment = new RepoAssessment
			{
				RepositoryFullName = row.RepositoryFullName,
				DefaultBranch = context.DefaultBranch,
				AssessedAtUtc = DateTimeOffset.UtcNow,
				RuleResults = results
			};

			row.CategorySummaries = BuildCategorySummaries(results);
			row.Status = PackageStatus.Assessed;
			row.StatusMessage = $"{row.TotalFailures} issue(s) found (local).";
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to locally assess {Repo}", row.RepositoryFullName);
			row.Status = PackageStatus.Error;
			row.StatusMessage = $"Local assessment failed: {ex.Message}";
		}
	}

	/// <summary>
	/// Generates an AI remediation prompt from failed rules.
	/// </summary>
	public static string GenerateRemediationPrompt(PackageDashboardRow row, bool includeInfo = true)
	{
		if (row.Assessment is null)
		{
			return string.Empty;
		}

		var failures = row.Assessment.RuleResults
			.Where(r => !r.Passed && (includeInfo || r.Severity != AssessmentSeverity.Info))
			.ToList();
		return GeneratePromptFromFailures(row, failures);
	}

	/// <summary>
	/// Generates an AI remediation prompt for a specific category's failed rules.
	/// </summary>
	public static string GenerateCategoryRemediationPrompt(PackageDashboardRow row, AssessmentCategory category, bool includeInfo = true)
	{
		if (row.Assessment is null)
		{
			return string.Empty;
		}

		var failures = row.Assessment.RuleResults
			.Where(r => !r.Passed && r.Category == category && (includeInfo || r.Severity != AssessmentSeverity.Info))
			.ToList();
		return GeneratePromptFromFailures(row, failures);
	}

	/// <summary>
	/// Generates an AI remediation prompt for a single failed rule.
	/// </summary>
	public static string GenerateRuleRemediationPrompt(PackageDashboardRow row, RuleResult result)
	{
		if (row.Assessment is null || result.Passed)
		{
			return string.Empty;
		}

		return GeneratePromptFromFailures(row, [result]);
	}

	private static string GeneratePromptFromFailures(PackageDashboardRow row, List<RuleResult> failures)
	{
		if (failures.Count == 0)
		{
			return string.Empty;
		}

		var lines = new List<string>
		{
			$"# Remediation Instructions for {row.PackageId}",
			$"Repository: {row.RepositoryFullName}",
			$"Local path: {row.LocalPath}",
			"",
			"Please fix the following governance issues:",
			""
		};

		foreach (var failure in failures)
		{
			lines.Add($"## [{failure.RuleId}] {failure.RuleName}");
			lines.Add($"- **Issue**: {failure.Message}");
			if (failure.Advisory is not null)
			{
				lines.Add($"- **Fix**: {failure.Advisory.Detail}");
				if (failure.Advisory.Data.Count > 0)
				{
					foreach (var (key, value) in failure.Advisory.Data)
					{
						lines.Add($"  - `{key}`: {FormatDataValue(value)}");
					}
				}
			}

			lines.Add("");
		}

		return string.Join('\n', lines);
	}

	/// <summary>
	/// Gets the remediation registry for checking fix availability.
	/// </summary>
	public RemediationRegistry RemediationRegistry { get; }

	/// <summary>
	/// Applies automatic file-based remediations for all failed rules that have
	/// a registered remediation.
	/// Returns the list of files created/modified.
	/// </summary>
	public Task<List<string>> ApplyRemediationsAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var applied = new List<string>();

		if (row.Assessment is null || row.LocalPath is null)
		{
			onOutput?.Invoke("⚠️ No assessment data or local path — cannot apply remediations.");
			return Task.FromResult(applied);
		}

		var failures = row.Assessment.RuleResults.Where(r => !r.Passed && r.Advisory is not null).ToList();

		// Ensure REPO-05 (Solution Items) runs last so it can pick up files created by other remediations
		var ordered = failures
			.OrderBy(f => f.RuleId == "REPO-05" ? 1 : 0)
			.ToList();

		foreach (var failure in ordered)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ApplySingleRemediation(row.LocalPath, failure, applied, onOutput);
		}

		if (applied.Count == 0)
		{
			onOutput?.Invoke("ℹ️ No auto-remediable issues found.");
		}
		else
		{
			onOutput?.Invoke($"✅ Applied {applied.Count} remediation(s).");
		}

		return Task.FromResult(applied);
	}

	/// <summary>
	/// Applies automatic remediations for a specific category.
	/// </summary>
	public Task<List<string>> ApplyCategoryRemediationsAsync(
		PackageDashboardRow row,
		AssessmentCategory category,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var applied = new List<string>();

		if (row.Assessment is null || row.LocalPath is null)
		{
			onOutput?.Invoke("⚠️ No assessment data or local path — cannot apply remediations.");
			return Task.FromResult(applied);
		}

		var failures = row.Assessment.RuleResults
			.Where(r => !r.Passed && r.Category == category && r.Advisory is not null)
			.ToList();

		foreach (var failure in failures)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ApplySingleRemediation(row.LocalPath, failure, applied, onOutput);
		}

		if (applied.Count == 0)
		{
			onOutput?.Invoke($"ℹ️ No auto-remediable issues found in {category}.");
		}

		return Task.FromResult(applied);
	}

	/// <summary>
	/// Checks if a specific failed rule can be auto-remediated via the registry.
	/// </summary>
	public bool IsAutoRemediable(RuleResult result)
		=> RemediationRegistry.CanRemediate(result);

	/// <summary>
	/// Public entry point for applying a single remediation from outside the service.
	/// </summary>
	public void ApplySingleRemediationPublic(
		string localPath,
		RuleResult failure,
		List<string> applied,
		Action<string>? onOutput)
		=> ApplySingleRemediation(localPath, failure, applied, onOutput);

	/// <summary>
	/// Applies a single remediation via the registry.
	/// </summary>
	private void ApplySingleRemediation(
		string localPath,
		RuleResult failure,
		List<string> applied,
		Action<string>? onOutput)
	{
		var remediation = RemediationRegistry.Get(failure.RuleId);
		if (remediation is null || !remediation.CanRemediate(failure))
		{
			return;
		}

		try
		{
			remediation.Apply(localPath, failure, applied, onOutput);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed remediation for rule {RuleId}", failure.RuleId);
			onOutput?.Invoke($"❌ [{failure.RuleId}] Failed: {ex.Message}");
		}
	}

	/// <summary>
	/// Builds a local repository.
	/// </summary>
	public async Task BuildAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.Building;
		row.StatusMessage = "Building...";

		var (success, _) = await _localRepo.BuildAsync(repoName, onOutput, cancellationToken).ConfigureAwait(false);

		row.Status = success ? PackageStatus.BuildSucceeded : PackageStatus.BuildFailed;
		row.StatusMessage = success ? "Build succeeded." : "Build failed.";
	}

	/// <summary>
	/// Syncs a local repository with remote (fetch, pull --rebase, push).
	/// </summary>
	public async Task GitSyncAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.GitSyncing;
		row.StatusMessage = "Syncing with remote...";

		var (success, _) = await _localRepo.GitSyncAsync(repoName, onOutput, cancellationToken).ConfigureAwait(false);

		if (success)
		{
			// Refresh git status after sync
			row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoName, cancellationToken).ConfigureAwait(false);
			row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoName, cancellationToken).ConfigureAwait(false);
			row.IsSyncedWithOrigin = true; // Just synced, so by definition in sync
		}

		row.Status = success ? PackageStatus.GitSynced : PackageStatus.Error;
		row.StatusMessage = success ? "Synced with remote." : "Git sync failed.";
	}

	/// <summary>
	/// Commits all local changes, fetches, rebases on remote, and pushes.
	/// Does not change the row status (preserves current workflow state).
	/// </summary>
	public async Task<bool> CommitAndPushAsync(
		PackageDashboardRow row,
		string commitMessage,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			onOutput?.Invoke("❌ Cannot determine repo name.");
			return false;
		}

		var (success, _) = await _localRepo.CommitAndPushAsync(repoName, commitMessage, onOutput, cancellationToken).ConfigureAwait(false);

		if (success)
		{
			// Refresh git status after push
			row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoName, cancellationToken).ConfigureAwait(false);
			row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoName, cancellationToken).ConfigureAwait(false);
			row.IsSyncedWithOrigin = true;
		}

		return success;
	}

	/// <summary>
	/// Refreshes the git status for a row (branch, working tree clean state, and sync status with origin).
	/// </summary>
	public async Task RefreshGitStatusAsync(PackageDashboardRow row, CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null || !row.IsClonedLocally)
		{
			return;
		}

		row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoName, cancellationToken).ConfigureAwait(false);
		row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoName, cancellationToken).ConfigureAwait(false);
		row.IsSyncedWithOrigin = await _localRepo.IsSyncedWithOriginAsync(repoName, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs tests on a local repository.
	/// </summary>
	public async Task RunTestsAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.Testing;
		row.StatusMessage = "Running tests...";

		var (success, _) = await _localRepo.RunTestsAsync(repoName, onOutput, cancellationToken).ConfigureAwait(false);

		row.Status = success ? PackageStatus.TestsPassed : PackageStatus.TestsFailed;
		row.StatusMessage = success ? "All tests passed." : "Tests failed.";
	}

	/// <summary>
	/// Runs the publish script on a local repository.
	/// </summary>
	public async Task RunPublishAsync(
		PackageDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.Publishing;
		row.StatusMessage = "Publishing...";

		var (success, _) = await _localRepo.RunPublishScriptAsync(repoName, onOutput, cancellationToken).ConfigureAwait(false);

		row.Status = success ? PackageStatus.Published : PackageStatus.Error;
		row.StatusMessage = success ? "Published successfully." : "Publish failed.";
	}

	/// <summary>
	/// Checks whether a NuGet package is still listed (not deprecated/de-listed).
	/// </summary>
	public Task<bool> IsPackageListedAsync(string packageId, CancellationToken cancellationToken = default)
		=> _nuget.IsPackageListedAsync(packageId, cancellationToken);

	internal static Dictionary<AssessmentCategory, CategorySummary> BuildCategorySummaries(List<RuleResult> results)
	{
		var summaries = new Dictionary<AssessmentCategory, CategorySummary>();

		foreach (var group in results.GroupBy(r => r.Category))
		{
			summaries[group.Key] = new CategorySummary
			{
				Passed = group.Count(r => r.Passed),
				Errors = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Error),
				Warnings = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Warning),
				Infos = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Info),
			};
		}

		return summaries;
	}

	private static string? ExtractRepoName(string? url)
	{
		if (url is null)
		{
			return null;
		}

		try
		{
			var uri = new Uri(url);
			var segments = uri.AbsolutePath.Trim('/').Split('/');
			return segments.Length >= 2 ? segments[1] : null;
		}
		catch
		{
			return null;
		}
	}

	private static string FormatDataValue(object value) => value switch
	{
		string s => s,
		string[] arr => string.Join(", ", arr),
		IEnumerable<object> list => string.Join(", ", list),
		_ => value.ToString() ?? string.Empty
	};
}
