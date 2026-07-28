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
	private readonly RegressionGuardService _regressionGuard;
	private readonly RuntimeSettingsService _runtimeSettings;
	private readonly AppSettings _settings;
	private readonly ILogger<DashboardService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="DashboardService"/> class.
	/// </summary>
	public DashboardService(
		NuGetDiscoveryService nuget,
		LocalRepoService localRepo,
		RemediationRegistry remediationRegistry,
		RegressionGuardService regressionGuard,
		RuntimeSettingsService runtimeSettings,
		IOptions<AppSettings> settings,
		ILogger<DashboardService> logger)
	{
		_nuget = nuget;
		_localRepo = localRepo;
		_regressionGuard = regressionGuard;
		_runtimeSettings = runtimeSettings;
		RemediationRegistry = remediationRegistry;
		_settings = settings.Value;
		_logger = logger;
	}

	/// <summary>
	/// Discovers all packages and builds initial dashboard rows.
	/// </summary>
	/// <param name="organization">
	/// When supplied, only this organisation is discovered, so rediscovering one organisation does not
	/// pay the cost of every other. When null, every configured organisation is discovered.
	/// </param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<List<PackageDashboardRow>> DiscoverPackagesAsync(
		string? organization = null,
		CancellationToken cancellationToken = default)
	{
		var organizations = organization is null
			? _runtimeSettings.Organizations
			: [organization];

		var packages = new List<NuGetPackageInfo>();

		// Discover each organisation in turn and concatenate. Sequential rather than parallel: this
		// runs once at start-up, and NuGet's search API is the shared resource being paged through.
		foreach (var owner in organizations)
		{
			var discovered = await _nuget
				.DiscoverOrganizationPackagesAsync(owner, cancellationToken)
				.ConfigureAwait(false);

			packages.AddRange(discovered);
		}

		var rows = new List<PackageDashboardRow>();

		foreach (var pkg in packages)
		{
			var repoName = pkg.RepositoryName;
			var isCloned = repoName is not null && _localRepo.IsClonedLocally(repoName);

			var row = new PackageDashboardRow
			{
				PackageId = pkg.PackageId,
				Organization = pkg.Organization,
				LatestVersion = pkg.LatestVersion,
				RepositoryFullName = repoName is not null ? $"{pkg.RepositoryOwner ?? pkg.Organization}/{repoName}" : null,
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
		catch (NotFoundException)
		{
			_logger.LogWarning("Repository {Repo} not found on GitHub (private, renamed, or wrong owner).", row.RepositoryFullName);
			row.Status = PackageStatus.Error;
			row.StatusMessage = $"Repository '{row.RepositoryFullName}' not found on GitHub (private, renamed, or wrong owner).";
		}
		catch (RateLimitExceededException)
		{
			_logger.LogWarning("GitHub rate limit hit while assessing {Repo}.", row.RepositoryFullName);
			row.Status = PackageStatus.Error;
			row.StatusMessage = "GitHub rate limit reached. Configure a Personal Access Token (AppSettings:GitHubPat) or wait a few minutes.";
		}
		catch (ApiException ex) when (ex.Message.Contains("abuse", StringComparison.OrdinalIgnoreCase)
			|| ex.Message.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase))
		{
			_logger.LogWarning("GitHub secondary rate limit hit while assessing {Repo}.", row.RepositoryFullName);
			row.Status = PackageStatus.Error;
			row.StatusMessage = "GitHub secondary rate limit reached. Configure a Personal Access Token (AppSettings:GitHubPat) or wait a few minutes.";
		}
		catch (AuthorizationException)
		{
			_logger.LogWarning("GitHub authorization failed while assessing {Repo}.", row.RepositoryFullName);
			row.Status = PackageStatus.Error;
			row.StatusMessage = "GitHub authentication failed. Check the token is valid and has 'repo' + 'read:org' scopes.";
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
			var repoName = row.RepositoryUrl is null ? null : ExtractRepoName(row.RepositoryUrl);
			if (!string.IsNullOrWhiteSpace(repoName))
			{
				row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoName, cancellationToken).ConfigureAwait(false);
			}

			var detectedDefaultBranch = !string.IsNullOrWhiteSpace(repoName)
				? await _localRepo.GetRemoteDefaultBranchAsync(repoName!, cancellationToken).ConfigureAwait(false)
				: null;
			var defaultBranch = string.IsNullOrWhiteSpace(detectedDefaultBranch)
				? row.Assessment?.DefaultBranch ?? "main"
				: detectedDefaultBranch;

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
			var context = localBuilder.Build(row.LocalPath, row.RepositoryFullName, repoOptions, defaultBranch, row.CurrentBranch);

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

	/// <summary>
	/// Gets the full Codacy issue report (markdown) for a repository, suitable for download as a
	/// standalone <c>.md</c> file or for pasting into an AI session. Returns null when the Codacy
	/// issues rule (CQ-05) did not run or found nothing to report.
	/// </summary>
	public static string? GetCodacyReportMarkdown(PackageDashboardRow row)
		=> row.Assessment?.RuleResults
			.FirstOrDefault(r => r.RuleId == "CQ-05" && r.Advisory is not null)
			?.Advisory?.Detail;

	/// <summary>
	/// Generates an AI remediation prompt from an explicit set of rule failures.
	/// </summary>
	public static string GenerateRemediationPromptForFailures(PackageDashboardRow row, IEnumerable<RuleResult> failures, bool includeInfo = false)
	{
		var filtered = failures
			.Where(r => !r.Passed && (includeInfo || r.Severity != AssessmentSeverity.Info))
			.ToList();

		return GeneratePromptFromFailures(row, filtered);
	}

	/// <summary>
	/// Generates an AI remediation prompt for a build or test workflow failure using recent console output.
	/// </summary>
	public static string GenerateWorkflowFailurePrompt(PackageDashboardRow row, string workflowArea, IEnumerable<string> consoleLines)
	{
		var excerpt = consoleLines
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.TakeLast(120)
			.ToList();

		var title = workflowArea.Equals("test", StringComparison.OrdinalIgnoreCase)
			? "Test Failure"
			: "Build Failure";

		var targetOutcome = workflowArea.Equals("test", StringComparison.OrdinalIgnoreCase)
			? "make the tests pass"
			: "make the project build successfully";

		var lines = new List<string>
		{
			$"# {title} Fix Instructions for {row.PackageId}",
			$"Repository: {row.RepositoryFullName}",
			$"Local path: {row.LocalPath}",
			$"Current status: {row.Status}",
			$"Requested outcome: {targetOutcome}.",
			"",
			"Please inspect the recent console output below, identify the root cause, apply the minimum code changes needed, and then rerun the failing step.",
			""
		};

		if (excerpt.Count > 0)
		{
			lines.Add("## Recent Console Output");
			lines.Add("```text");
			lines.AddRange(excerpt);
			lines.Add("```");
		}
		else if (!string.IsNullOrWhiteSpace(row.StatusMessage))
		{
			lines.Add("## Failure Summary");
			lines.Add(row.StatusMessage);
		}

		return string.Join('\n', lines);
	}

	/// <summary>
	/// Generates a concise AI remediation prompt for build/test failures using high-signal log lines.
	/// </summary>
	public static string GenerateConciseWorkflowFailurePrompt(PackageDashboardRow row, string workflowArea, IEnumerable<string> consoleLines)
	{
		var isTest = workflowArea.Equals("test", StringComparison.OrdinalIgnoreCase);
		var title = isTest ? "Test Failure" : "Build Failure";
		var action = isTest ? "dotnet test" : "dotnet build";

		var keyLines = consoleLines
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.Where(line =>
				line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
				line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
				line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
				line.Contains("assert", StringComparison.OrdinalIgnoreCase))
			.TakeLast(24)
			.ToList();

		if (keyLines.Count == 0)
		{
			keyLines = consoleLines
				.Where(line => !string.IsNullOrWhiteSpace(line))
				.TakeLast(24)
				.ToList();
		}

		var lines = new List<string>
		{
			$"# {title} Fix Request",
			$"Repository: {row.RepositoryFullName}",
			$"Local path: {row.LocalPath}",
			"",
			$"Please fix the root cause of the {workflowArea} failure with the minimum safe code changes, then rerun `{action}`.",
			"",
			"## Key Output",
			"```text"
		};

		if (keyLines.Count > 0)
		{
			lines.AddRange(keyLines);
		}
		else if (!string.IsNullOrWhiteSpace(row.StatusMessage))
		{
			lines.Add(row.StatusMessage);
		}

		lines.Add("```");
		return string.Join('\n', lines);
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

		// Ensure REPO-06 (default branch) runs first and REPO-05 (Solution Items) runs last.
		var ordered = failures
			.OrderBy(f => f.RuleId switch
			{
				"REPO-06" => 0,
				"REPO-05" => 2,
				_ => 1
			})
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

		var isClonedLocally = _localRepo.IsClonedLocally(repoName);
		if (!isClonedLocally)
		{
			onOutput?.Invoke($"Cloning {repoName}...");
			var cloneUrl = $"https://github.com/{_settings.GitHubOrganization}/{repoName}.git";
			var (cloneSuccess, cloneOutput) = await _localRepo.CloneAsync(cloneUrl, repoName, onOutput, cancellationToken).ConfigureAwait(false);
			if (!cloneSuccess)
			{
				row.Status = PackageStatus.Error;
				row.StatusMessage = "Git sync failed.";
				onOutput?.Invoke(cloneOutput);
				return;
			}

			row.IsClonedLocally = true;
			row.LocalPath = _localRepo.GetLocalPath(repoName);
			onOutput?.Invoke($"Cloned to {row.LocalPath}");
		}

		row.Status = PackageStatus.GitSyncing;
		row.StatusMessage = "Syncing with remote...";

		var (success, _) = await _localRepo.GitSyncAsync(repoName, onOutput, cancellationToken).ConfigureAwait(false);

		await RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);

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

	// ── Issue-centric ("dimensional flip") view + bulk apply across repositories ──

	/// <summary>
	/// Builds the issue-centric view (Category → Rule → Repository) from the assessed rows,
	/// marking each occurrence's auto-remediability via the remediation registry.
	/// </summary>
	public IssueCentricView BuildIssueCentricView(IEnumerable<PackageDashboardRow> rows)
	{
		// Pass the package id through: a repository hosting several packages appears once per package
		// under a rule, and this is what tells those occurrences apart in the issue tree.
		var entries = rows
			.Where(r => r.RepositoryFullName is not null && r.Assessment is not null)
			.Select(r => new AssessedPackage(r.RepositoryFullName!, r.Assessment!, r.PackageId));
		return IssueCentricViewBuilder.Build(entries, IsAutoRemediable);
	}

	/// <summary>
	/// Generates a single consolidated AI prompt for one issue class across every affected repository.
	/// </summary>
	public string GenerateCombinedRulePrompt(IEnumerable<PackageDashboardRow> rows, string ruleId)
	{
		var issueClass = BuildIssueCentricView(rows).AllIssueClasses
			.FirstOrDefault(i => string.Equals(i.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
		return issueClass is null ? string.Empty : CombinedRemediationPromptBuilder.ForRule(issueClass);
	}

	/// <summary>
	/// Generates a single consolidated AI prompt for a category across every affected repository.
	/// </summary>
	public string GenerateCombinedCategoryPrompt(IEnumerable<PackageDashboardRow> rows, AssessmentCategory category, bool onlyNonRemediable = true)
	{
		var group = BuildIssueCentricView(rows).Categories.FirstOrDefault(c => c.Category == category);
		return group is null ? string.Empty : CombinedRemediationPromptBuilder.ForCategory(group, onlyNonRemediable);
	}

	/// <summary>
	/// Applies a single rule's remediation across every affected repository: sync → reassess →
	/// apply → commit → push. Stops on the first failure. Only repositories where the rule is failing
	/// are touched.
	/// </summary>
	public Task<BulkApplyOutcome> ApplyRuleAcrossReposAsync(
		IEnumerable<PackageDashboardRow> rows,
		string ruleId,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var affected = rows.Where(r => RepoHasFailingRule(r, ruleId)).ToList();
		return ApplyAcrossReposAsync(
			affected,
			row => ApplySingleRuleAsync(row, ruleId, onOutput),
			$"chore: apply {ruleId} governance remediation",
			onOutput,
			cancellationToken);
	}

	/// <summary>
	/// Applies all auto-remediable rules in a category across every affected repository.
	/// </summary>
	public Task<BulkApplyOutcome> ApplyCategoryAcrossReposAsync(
		IEnumerable<PackageDashboardRow> rows,
		AssessmentCategory category,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var affected = rows.Where(r => RepoHasFailingCategory(r, category)).ToList();
		return ApplyAcrossReposAsync(
			affected,
			row => ApplyCategoryRemediationsAsync(row, category, onOutput, cancellationToken),
			$"chore: apply {category} governance remediations",
			onOutput,
			cancellationToken);
	}

	/// <summary>
	/// Applies every auto-remediable rule across every affected repository (the global "fix everything").
	/// </summary>
	public Task<BulkApplyOutcome> ApplyEverythingAcrossReposAsync(
		IEnumerable<PackageDashboardRow> rows,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var affected = rows
			.Where(r => r.RepositoryFullName is not null
				&& r.Assessment?.RuleResults.Any(rr => !rr.Passed && IsAutoRemediable(rr)) == true)
			.ToList();
		return ApplyAcrossReposAsync(
			affected,
			row => ApplyRemediationsAsync(row, onOutput, cancellationToken),
			"chore: apply governance remediations",
			onOutput,
			cancellationToken);
	}

	private Task<List<string>> ApplySingleRuleAsync(PackageDashboardRow row, string ruleId, Action<string>? onOutput)
	{
		var applied = new List<string>();
		var fresh = row.Assessment?.RuleResults
			.FirstOrDefault(rr => !rr.Passed && string.Equals(rr.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
		if (fresh is not null && row.LocalPath is not null && IsAutoRemediable(fresh))
		{
			ApplySingleRemediationPublic(row.LocalPath, fresh, applied, onOutput);
		}

		return Task.FromResult(applied);
	}

	private async Task<BulkApplyOutcome> ApplyAcrossReposAsync(
		List<PackageDashboardRow> affected,
		Func<PackageDashboardRow, Task<List<string>>> applyFunc,
		string commitMessage,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var outcome = new BulkApplyOutcome();
		onOutput?.Invoke($"Applying across {affected.Count} repositor{(affected.Count == 1 ? "y" : "ies")}...");

		foreach (var row in affected)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = row.RepositoryFullName ?? row.PackageId;
			onOutput?.Invoke($"── {name} ──");

			try
			{
				// Guardrail: never commit onto a stale clone — sync, then reassess the fresh tree.
				await GitSyncAsync(row, onOutput, cancellationToken).ConfigureAwait(false);
				await AssessLocalRepositoryAsync(row, cancellationToken).ConfigureAwait(false);

				var applied = await applyFunc(row).ConfigureAwait(false);
				if (applied.Count == 0)
				{
					outcome.Results.Add(new RepoApplyResult
					{
						RepositoryFullName = name,
						Status = RepoApplyStatus.NothingToDo,
						Message = "No changes to apply after sync."
					});
					continue;
				}

				var pushed = await CommitAndPushAsync(row, commitMessage, onOutput, cancellationToken).ConfigureAwait(false);
				outcome.Results.Add(new RepoApplyResult
				{
					RepositoryFullName = name,
					Status = pushed ? RepoApplyStatus.Pushed : RepoApplyStatus.Failed,
					Message = pushed ? $"{applied.Count} file(s) committed & pushed." : "Commit/push failed."
				});

				if (pushed && row.RepositoryFullName is not null)
				{
					// Verify the build in the background; auto-revert if our change broke it.
					_regressionGuard.Enqueue(row.RepositoryFullName);
					onOutput?.Invoke($"🛡️ Queued {row.RepositoryFullName} for build verification.");
				}

				if (!pushed)
				{
					outcome.StoppedOnFailure = true;
					onOutput?.Invoke("⛔ Stopping bulk apply: commit/push failed.");
					break;
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				outcome.Results.Add(new RepoApplyResult
				{
					RepositoryFullName = name,
					Status = RepoApplyStatus.Failed,
					Message = ex.Message
				});
				outcome.StoppedOnFailure = true;
				onOutput?.Invoke($"⛔ Stopping bulk apply: {ex.Message}");
				break;
			}
		}

		return outcome;
	}

	private static bool RepoHasFailingRule(PackageDashboardRow row, string ruleId)
		=> row.RepositoryFullName is not null
			&& row.Assessment?.RuleResults.Any(rr => !rr.Passed && string.Equals(rr.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)) == true;

	private static bool RepoHasFailingCategory(PackageDashboardRow row, AssessmentCategory category)
		=> row.RepositoryFullName is not null
			&& row.Assessment?.RuleResults.Any(rr => !rr.Passed && rr.Category == category) == true;

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
		row.LatestTag = await _localRepo.GetLatestTagAsync(repoName, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Returns a short preview of dirty working tree lines for diagnostics in UI output.
	/// </summary>
	public async Task<IReadOnlyList<string>> GetWorkingTreeStatusPreviewAsync(
		PackageDashboardRow row,
		int maxLines = 3,
		CancellationToken cancellationToken = default)
	{
		var repoName = ExtractRepoName(row.RepositoryUrl);
		if (repoName is null || !row.IsClonedLocally)
		{
			return [];
		}

		return await _localRepo.GetWorkingTreeStatusPreviewAsync(repoName, maxLines, cancellationToken).ConfigureAwait(false);
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
				Criticals = group.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Critical),
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
			if (segments.Length < 2)
			{
				return null;
			}

			var name = segments[1];
			return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
				? name[..^4]
				: name;
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
