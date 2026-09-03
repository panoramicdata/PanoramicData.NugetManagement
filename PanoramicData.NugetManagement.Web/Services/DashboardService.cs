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
public class DashboardService
{
	private readonly NuGetDiscoveryService _nuget;
	private readonly PublishedVersionRefresher _publishedVersions;
	private readonly DashboardCacheService _cache;
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
		PublishedVersionRefresher publishedVersions,
		DashboardCacheService cache,
		LocalRepoService localRepo,
		RemediationRegistry remediationRegistry,
		RegressionGuardService regressionGuard,
		RuntimeSettingsService runtimeSettings,
		IOptions<AppSettings> settings,
		ILogger<DashboardService> logger)
	{
		_nuget = nuget;
		_publishedVersions = publishedVersions;
		_cache = cache;
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
	/// <param name="github">
	/// A client for asking GitHub what each declared repository is really called. When null the names
	/// packages declare are taken at their word, which is what discovery did before.
	/// </param>
	public async Task<List<RepositoryDashboardRow>> DiscoverPackagesAsync(
		string? organization = null,
		CancellationToken cancellationToken = default,
		IGitHubClient? github = null)
	{
		var organizations = organization is null
			? _runtimeSettings.Organizations
			: [organization];

		var packages = new List<NuGetPackageInfo>();

		using var canonicalLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
		var canonicalNames = github is null
			? null
			: new CanonicalRepositoryLookup(
				github,
				canonicalLoggerFactory.CreateLogger<CanonicalRepositoryLookup>());

		// Discover each organisation in turn and concatenate. Sequential rather than parallel: this
		// runs once at start-up, and NuGet's search API is the shared resource being paged through.
		foreach (var owner in organizations)
		{
			var discovered = await _nuget
				.DiscoverOrganizationPackagesAsync(owner, cancellationToken, canonicalNames)
				.ConfigureAwait(false);

			packages.AddRange(discovered);
		}

		// Whether a repository is ours at all is decided before anything is read from disk on the
		// strength of it: NuGet's owner: search says who owns the package, never who owns the
		// repository behind it. Every organisation under management is consulted, not merely the one
		// being discovered: refreshing one must not decide that another's repositories belong to
		// somebody else.
		var (rows, ungoverned) = BuildRows(
			packages,
			_cache.GetCachedRows() ?? [],
			_runtimeSettings.Organizations);

		_cache.SetUngovernedPackages(ungoverned);

		foreach (var row in rows)
		{
			// Local paths are keyed on the full owner/name identity, not the bare repository name.
			var isCloned = _localRepo.IsClonedLocally(row.RepositoryFullName);

			row.IsClonedLocally = isCloned;
			row.LocalPath = _localRepo.GetLocalPath(row.RepositoryFullName);
			row.SlnxPath = isCloned ? _localRepo.FindSlnxFile(row.RepositoryFullName) : null;
			row.Status = isCloned ? PackageStatus.NotAssessed : PackageStatus.NotCloned;

			if (isCloned)
			{
				row.CurrentBranch = await _localRepo
					.GetCurrentBranchAsync(row.RepositoryFullName, cancellationToken)
					.ConfigureAwait(false);

				row.IsWorkingTreeClean = await _localRepo
					.IsWorkingTreeCleanAsync(row.RepositoryFullName, cancellationToken)
					.ConfigureAwait(false);
			}
		}

		return rows;
	}

	/// <summary>
	/// Turns discovered packages into one row per repository, plus the packages that belong to no
	/// repository we govern.
	/// </summary>
	/// <remarks>
	/// Static and free of the network so the grouping — the part with the interesting edge cases — can
	/// be tested without one.
	/// </remarks>
	/// <param name="packages">The packages discovered from NuGet.</param>
	/// <param name="previousRows">The rows from the last successful discovery, for carry-forward.</param>
	/// <param name="organizations">The organisations under management.</param>
	internal static (List<RepositoryDashboardRow> Rows, List<UngovernedPackage> Ungoverned) BuildRows(
		IReadOnlyList<NuGetPackageInfo> packages,
		IReadOnlyList<RepositoryDashboardRow> previousRows,
		IReadOnlyList<string> organizations)
	{
		// A package whose nuspec we could not read keeps the repository we knew it by. Without this a
		// request going astray removes a repository from governance, which is how eight of them came to
		// be reported as declaring no repository at all.
		var previousByPackageId = previousRows
			.SelectMany(row => row.Packages.Select(package => (package.PackageId, row.RepositoryFullName)))
			.GroupBy(pair => pair.PackageId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => group.First().RepositoryFullName,
				StringComparer.OrdinalIgnoreCase);

		var rows = new Dictionary<string, RepositoryDashboardRow>(StringComparer.OrdinalIgnoreCase);
		var ungoverned = new List<UngovernedPackage>();

		foreach (var package in packages)
		{
			var identity = IdentifyRepository(package, previousByPackageId);

			var reason = identity is null
				? ReasonForNoRepository(package)
				: GovernanceScope.ReasonNotGoverned(identity, organizations);

			if (reason is not null)
			{
				ungoverned.Add(new UngovernedPackage
				{
					PackageId = package.PackageId,
					Organization = package.Organization,
					DeclaredRepository = identity,
					Reason = reason
				});

				continue;
			}

			if (!rows.TryGetValue(identity!, out var row))
			{
				row = new RepositoryDashboardRow
				{
					RepositoryFullName = identity!,
					Organization = package.Organization,
					RepositoryUrl = package.RepositoryUrl ?? $"https://github.com/{identity}"
				};

				rows[identity!] = row;
			}

			row.Packages.Add(new PublishedPackage
			{
				PackageId = package.PackageId,
				LatestVersion = package.LatestVersion
			});
		}

		foreach (var row in rows.Values)
		{
			row.Packages.Sort((left, right) =>
				string.Compare(left.PackageId, right.PackageId, StringComparison.OrdinalIgnoreCase));
		}

		return (
			[.. rows.Values.OrderBy(row => row.RepositoryFullName, StringComparer.OrdinalIgnoreCase)],
			ungoverned);
	}

	/// <summary>
	/// The repository a package belongs to, or null when it belongs to none we can name.
	/// </summary>
	private static string? IdentifyRepository(
		NuGetPackageInfo package,
		Dictionary<string, string> previousByPackageId)
	{
		if (package.RepositoryName is not null)
		{
			return $"{package.RepositoryOwner ?? package.Organization}/{package.RepositoryName}";
		}

		return package.ResolutionOutcome is RepositoryResolutionOutcome.LookupFailed
			&& previousByPackageId.TryGetValue(package.PackageId, out var previous)
				? previous
				: null;
	}

	/// <summary>
	/// Why a package with no repository has none — distinguishing a nuspec that declares nothing from
	/// one we never managed to read.
	/// </summary>
	private static string ReasonForNoRepository(NuGetPackageInfo package)
		=> package.ResolutionOutcome is RepositoryResolutionOutcome.LookupFailed
			? $"{UngovernedPackage.LookupFailedReasonPrefix} (network) — rediscover to try again."
			: "The package declares no repository in its nuspec.";

	/// <summary>
	/// Re-derives each row's local clone facts from what is actually on disk, leaving its assessment
	/// alone. Returns the number of rows whose cloned state changed.
	/// </summary>
	/// <remarks>
	/// The cache stores whether a repository was cloned, but nothing kept that in step with the disk: a
	/// folder deleted (or a clone root reconfigured) since the last refresh left rows claiming a checkout
	/// that is not there, and the toolbar offering to build and push it. The write guards fail closed on
	/// such a row, so the risk was a misleading display rather than a bad write — but "cloned" is exactly
	/// the count the estate view leads on, so it should be true.
	/// </remarks>
	public async Task<int> ReconcileLocalStateAsync(
		IEnumerable<RepositoryDashboardRow> rows,
		CancellationToken cancellationToken = default)
	{
		var changed = 0;

		var governed = _runtimeSettings.Organizations;

		foreach (var row in rows)
		{
			// Rules change, and a cached row can predate the ones in force. Re-judging here means a
			// repository that stopped being ours cannot keep its clone facts — and so its buttons —
			// merely by having been governed when the cache was written.
			GovernanceScope.Apply(row, governed);

			var repoIdentity = RepoIdentity(row);
			if (repoIdentity is null || !row.IsGoverned)
			{
				continue;
			}

			row.LocalPath = _localRepo.GetLocalPath(repoIdentity);

			var isCloned = _localRepo.IsClonedLocally(repoIdentity);
			if (isCloned == row.IsClonedLocally)
			{
				continue;
			}

			row.IsClonedLocally = isCloned;
			row.SlnxPath = isCloned ? _localRepo.FindSlnxFile(repoIdentity) : null;
			changed++;

			if (isCloned)
			{
				// Read the facts the toolbar gates on, or a newly-found clone would sit there with an
				// unknown branch and every local action refusing to run until something else filled it in.
				row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
				row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				// Everything below was read out of a checkout that is no longer there.
				row.CurrentBranch = null;
				row.IsWorkingTreeClean = null;
				row.IsSyncedWithOrigin = null;
				row.SyncStatusCheckedAtUtc = null;
				row.LatestTag = null;
			}
		}

		return changed;
	}

	/// <summary>
	/// The per-repository assessment options, as configured for this installation.
	/// </summary>
	/// <remarks>
	/// One place, because three callers now need the same answer — the two assessment paths and
	/// Dependabot triage — and an option added to one of them but not the others would assess the
	/// same repository differently depending on which path reached it.
	/// </remarks>
	private RepoOptions BuildRepoOptions()
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

		return repoOptions;
	}

	/// <summary>
	/// Builds the repository context Dependabot triage reads declared versions from, preferring the
	/// local clone when there is one.
	/// </summary>
	/// <param name="row">The repository to read.</param>
	/// <param name="github">A client for the remote path.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <remarks>
	/// The same choice the two assessment paths make, and for the same reason: a cloned repository can
	/// be read from disk for nothing, and only a repository with no clone needs the network.
	/// </remarks>
	public async Task<RepositoryContext> BuildContextAsync(
		RepositoryDashboardRow row,
		IGitHubClient github,
		CancellationToken cancellationToken = default)
	{
		var repoOptions = BuildRepoOptions();

		using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());

		if (row is { IsClonedLocally: true, LocalPath: not null })
		{
			return new LocalRepositoryContextBuilder(
					loggerFactory.CreateLogger<LocalRepositoryContextBuilder>())
				.Build(
					row.LocalPath,
					row.RepositoryFullName,
					repoOptions,
					"main",
					row.CurrentBranch,
					row.LatestTag,
					row.PrimaryPackage?.LatestVersion);
		}

		var parts = row.RepositoryFullName.Split('/');

		if (parts.Length != 2)
		{
			throw new InvalidOperationException(
				$"'{row.RepositoryFullName}' is not an owner/name repository.");
		}

		var repository = await github.Repository.Get(parts[0], parts[1]).ConfigureAwait(false);

		using var contextBuilder = new RepositoryContextBuilder(
			github,
			loggerFactory.CreateLogger<RepositoryContextBuilder>());

		return await contextBuilder
			.BuildAsync(repository, repoOptions, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Assesses a single repository against all governance rules using GitHub API.
	/// </summary>
	public async Task AssessRepositoryAsync(
		RepositoryDashboardRow row,
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
			// Read what is actually on nuget.org now. Discovery is the only other thing that writes
			// this, and Re-assess skips discovery, so without this CI-11 compares a fresh tag against
			// an hours-old version and cannot be talked out of it.
			await _publishedVersions.RefreshAsync(row, cancellationToken).ConfigureAwait(false);

			var parts = row.RepositoryFullName.Split('/');
			if (parts.Length != 2)
			{
				row.Status = PackageStatus.Error;
				row.StatusMessage = "Invalid repository full name.";
				return;
			}

			var repo = await github.Repository.Get(parts[0], parts[1]).ConfigureAwait(false);
			var repoOptions = BuildRepoOptions();

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

			await RefreshOpenIssuesAsync(row, github, parts[0], parts[1], cancellationToken).ConfigureAwait(false);

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
	/// Reads the repository's open issues and pull requests onto the row, and records whether the
	/// inbox was actually read.
	/// </summary>
	/// <param name="row">The row to populate.</param>
	/// <param name="github">The GitHub client to read through.</param>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <remarks>
	/// Fetched as part of an assessment rather than on its own schedule so there is one refresh path,
	/// one cache and one staleness window. A failure to read the inbox must not fail the assessment:
	/// the rules have already been evaluated by the time this runs, and losing them because a comment
	/// endpoint misbehaved would be a poor trade.
	/// <para>
	/// Rate-limit and authorisation failures are deliberately NOT swallowed here. They derive from
	/// <see cref="ApiException"/>, they say nothing about the inbox in particular, and the callers'
	/// handlers turn them into a message telling the user what to do. Reporting them as an empty inbox
	/// would show a healthy-looking repository whose issues had quietly gone missing.
	/// </para>
	/// <para>
	/// Both assessment paths call this one method so that the catch filter cannot drift between them.
	/// A swallowed failure leaves <see cref="RepositoryDashboardRow.OpenIssuesKnown"/> false, so the
	/// tree draws the unknown grey rather than a false green.
	/// </para>
	/// </remarks>
	private async Task RefreshOpenIssuesAsync(
		RepositoryDashboardRow row,
		IGitHubClient github,
		string owner,
		string name,
		CancellationToken cancellationToken)
	{
		try
		{
			var issueService = new RepositoryIssueService(new OctokitGitHubIssueApi(github));
			row.OpenIssues = [.. await issueService
				.GetOpenIssuesAsync(owner, name, cancellationToken)
				.ConfigureAwait(false)];
			row.OpenIssuesKnown = true;
		}
		catch (ApiException ex) when (ex is not RateLimitExceededException
			&& ex is not AuthorizationException
			&& !ex.Message.Contains("abuse", StringComparison.OrdinalIgnoreCase)
			&& !ex.Message.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase))
		{
			_logger.LogWarning(
				ex,
				"Could not read open issues for {Repo}; its inbox will show as unknown.",
				row.RepositoryFullName);
			row.OpenIssues = [];
			row.OpenIssuesKnown = false;
		}
	}

	/// <summary>
	/// Assesses a single repository against all governance rules using the local filesystem.
	/// This reads files directly from disk so that changes made by remediations are
	/// immediately visible without pushing to GitHub first.
	/// </summary>
	/// <param name="row">The row to assess.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <param name="github">
	/// A GitHub client, when the caller has one. Supplied, the repository's open issues and pull
	/// requests are read as well, exactly as the remote path reads them. Omitted, both
	/// <see cref="RepositoryDashboardRow.OpenIssues"/> and
	/// <see cref="RepositoryDashboardRow.OpenIssuesKnown"/> are left as they were.
	/// </param>
	public async Task AssessLocalRepositoryAsync(
		RepositoryDashboardRow row,
		CancellationToken cancellationToken = default,
		IGitHubClient? github = null)
	{
		if (row.RepositoryFullName is null || row.LocalPath is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "No repository or local path identified.";
			return;
		}

		// Captured so that a stopped assessment can put them back. A Stop is not a result, and the row
		// must not be left asserting one — neither the "Assessing" it is about to claim, which would
		// spin for ever, nor an error the user caused by pressing the button.
		var statusBeforeAssessing = row.Status;
		var statusMessageBeforeAssessing = row.StatusMessage;

		row.Status = PackageStatus.Assessing;
		row.StatusMessage = "Assessing (local)...";

		try
		{
			// See the note in AssessRepositoryAsync: the published version has to be re-read here too,
			// or Re-assess can never change CI-11's mind about a release that has landed.
			await _publishedVersions.RefreshAsync(row, cancellationToken).ConfigureAwait(false);

			var repoIdentity = RepoIdentity(row);
			if (!string.IsNullOrWhiteSpace(repoIdentity))
			{
				row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);

				// Read here rather than relying on a git-status refresh having happened: CI-11 compares
				// this with what is on nuget.org, and without it the rule has nothing to say. A local
				// `git describe` on a clone we already have is cheap.
				row.LatestTag = await _localRepo.GetLatestTagAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
			}

			var detectedDefaultBranch = !string.IsNullOrWhiteSpace(repoIdentity)
				? await _localRepo.GetRemoteDefaultBranchAsync(repoIdentity!, cancellationToken).ConfigureAwait(false)
				: null;
			var defaultBranch = string.IsNullOrWhiteSpace(detectedDefaultBranch)
				? row.Assessment?.DefaultBranch ?? "main"
				: detectedDefaultBranch;

			var repoOptions = BuildRepoOptions();

			using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
			var localBuilder = new LocalRepositoryContextBuilder(loggerFactory.CreateLogger<LocalRepositoryContextBuilder>());
			// The release facts the dashboard already knows, handed to the rules so CI-11 can compare
			// what was tagged with what was actually published.
			var context = localBuilder.Build(
				row.LocalPath,
				row.RepositoryFullName,
				repoOptions,
				defaultBranch,
				row.CurrentBranch,
				row.LatestTag,
				row.PrimaryPackage?.LatestVersion);

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

			// The local path is the one every actively-worked repository takes, so without this it
			// would be exactly those repositories whose inbox was never read.
			if (github is not null)
			{
				var identityParts = row.RepositoryFullName.Split('/');
				if (identityParts.Length == 2)
				{
					await RefreshOpenIssuesAsync(row, github, identityParts[0], identityParts[1], cancellationToken)
						.ConfigureAwait(false);
				}
			}

			row.Status = PackageStatus.Assessed;
			row.StatusMessage = $"{row.TotalFailures} issue(s) found (local).";
		}
		catch (OperationCanceledException)
		{
			// Stopping is not failing. Swallowed by the general catch below, a Stop became a cached RED
			// repository that outlived the click and hid the real state until the next successful
			// assessment — and, because nothing was ever thrown, the caller's own cancellation handling
			// never ran either, so a part-applied fix was left unreverted. The row goes back to what it
			// was before this pass started: the previous assessment, if there was one, is exactly as
			// true as it was a moment ago.
			row.Status = statusBeforeAssessing;
			row.StatusMessage = statusMessageBeforeAssessing;
			throw;
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
	public static string GenerateRemediationPrompt(RepositoryDashboardRow row, bool includeInfo = true)
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
	public static string GenerateCategoryRemediationPrompt(RepositoryDashboardRow row, AssessmentCategory category, bool includeInfo = true)
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
	public static string GenerateRuleRemediationPrompt(RepositoryDashboardRow row, RuleResult result)
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
	public static string? GetCodacyReportMarkdown(RepositoryDashboardRow row)
		=> row.Assessment?.RuleResults
			.FirstOrDefault(r => r.RuleId == "CQ-05" && r.Advisory is not null)
			?.Advisory?.Detail;

	/// <summary>
	/// Generates an AI remediation prompt from an explicit set of rule failures.
	/// </summary>
	public static string GenerateRemediationPromptForFailures(RepositoryDashboardRow row, IEnumerable<RuleResult> failures, bool includeInfo = false)
	{
		var filtered = failures
			.Where(r => !r.Passed && (includeInfo || r.Severity != AssessmentSeverity.Info))
			.ToList();

		return GeneratePromptFromFailures(row, filtered);
	}

	/// <summary>
	/// Generates an AI remediation prompt for a build or test workflow failure using recent console output.
	/// </summary>
	public static string GenerateWorkflowFailurePrompt(RepositoryDashboardRow row, string workflowArea, IEnumerable<string> consoleLines)
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
			$"# {title} Fix Instructions for {row.RepositoryFullName}",
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
	public static string GenerateConciseWorkflowFailurePrompt(RepositoryDashboardRow row, string workflowArea, IEnumerable<string> consoleLines)
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

	/// <summary>
	/// Orders rule failures worst-first: Critical and Error, then Warning, then everything else.
	/// Shared by the prompt builder and the on-screen grouped failure list so the two cannot drift.
	/// </summary>
	public static int SeverityRank(AssessmentSeverity severity) => severity switch
	{
		AssessmentSeverity.Critical or AssessmentSeverity.Error => 0,
		AssessmentSeverity.Warning => 1,
		_ => 2
	};

	/// <summary>
	/// Orders a category by the severity of its worst failure, so the most serious category leads.
	/// </summary>
	public static int CategoryRank(IEnumerable<RuleResult> failures)
		=> failures.Select(f => SeverityRank(f.Severity)).DefaultIfEmpty(2).Min();

	private static string GeneratePromptFromFailures(RepositoryDashboardRow row, List<RuleResult> failures)
	{
		if (failures.Count == 0)
		{
			return string.Empty;
		}

		var groups = failures
			.GroupBy(f => f.Category)
			.Select(g => new { Category = g.Key, Failures = g.OrderBy(f => SeverityRank(f.Severity)).ThenBy(f => f.RuleId).ToList() })
			.OrderBy(g => CategoryRank(g.Failures))
			.ThenBy(g => g.Category.ToString(), StringComparer.Ordinal)
			.ToList();

		// A single category needs no section headings — keep the flat form the per-category and
		// per-rule prompts have always produced. Multiple categories get sections so the AI sees
		// the whole picture, grouped and ordered the same way the UI presents it.
		var grouped = groups.Count > 1;

		var lines = new List<string>
		{
			$"# Remediation Instructions for {row.RepositoryFullName}",
			$"Repository: {row.RepositoryFullName}",
			$"Local path: {row.LocalPath}",
			""
		};

		if (grouped)
		{
			lines.Add("Please fix the following governance issues, grouped by category and ordered by severity — start with the most serious.");
		}
		else
		{
			lines.Add("Please fix the following governance issues:");
		}

		lines.Add("");

		var ruleHeadingPrefix = grouped ? "###" : "##";

		foreach (var group in groups)
		{
			if (grouped)
			{
				var count = group.Failures.Count;
				lines.Add($"## {group.Category} ({count} {(count == 1 ? "issue" : "issues")})");
				lines.Add("");
			}

			foreach (var failure in group.Failures)
			{
				lines.Add($"{ruleHeadingPrefix} [{failure.RuleId}] {failure.RuleName}");
				lines.Add($"- **Issue**: {failure.Message}");
				if (failure.Advisory is not null)
				{
					lines.Add($"- **Fix**: {failure.Advisory.Detail}");
					if (failure.Advisory.Data.Count > 0)
					{
						foreach (var (key, value) in failure.Advisory.Data)
						{
							// Nested facts — CQ-06's per-file grades, and every rule that reports a
							// list of dictionaries — are expanded rather than joined, so the model
							// gets the figures instead of a row of type names.
							lines.AddRange(AiFixPrompt
								.RenderFact(key, value, "  ", markdown: true)
								.Split('\n', StringSplitOptions.RemoveEmptyEntries)
								.Select(line => line.TrimEnd('\r')));
						}
					}
				}

				lines.Add("");
			}
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
	public async Task<List<string>> ApplyRemediationsAsync(
		RepositoryDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var applied = new List<string>();

		if (row.Assessment is null || row.LocalPath is null)
		{
			onOutput?.Invoke("⚠️ No assessment data or local path — cannot apply remediations.");
			return applied;
		}

		if (!await VerifyWritableCloneAsync(row, onOutput, cancellationToken).ConfigureAwait(false))
		{
			return applied;
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

		return applied;
	}

	/// <summary>
	/// Applies automatic remediations for a specific category.
	/// </summary>
	public async Task<List<string>> ApplyCategoryRemediationsAsync(
		RepositoryDashboardRow row,
		AssessmentCategory category,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var applied = new List<string>();

		if (row.Assessment is null || row.LocalPath is null)
		{
			onOutput?.Invoke("⚠️ No assessment data or local path — cannot apply remediations.");
			return applied;
		}

		if (!await VerifyWritableCloneAsync(row, onOutput, cancellationToken).ConfigureAwait(false))
		{
			return applied;
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

		return applied;
	}

	/// <summary>
	/// Checks if a specific failed rule can be auto-remediated via the registry.
	/// </summary>
	/// <remarks>
	/// A synthesised Dependabot finding is always actionable: its fix is a triage pass, which is always
	/// available and needs neither a clone nor an advisory. It has no <see cref="Remediations.IRemediation"/>
	/// and never will — that interface is synchronous and writes to the filesystem, and closing a pull
	/// request is neither — so asking the registry about it would answer "no" and hide the action.
	/// </remarks>
	public bool IsAutoRemediable(RuleResult result)
		=> DependabotIssueSynthesizer.IsSynthetic(result.RuleId)
			|| RemediationRegistry.CanRemediate(result);

	/// <summary>
	/// Applies one rule's remediation to a repository, and returns what it changed.
	/// </summary>
	/// <param name="row">The repository to write to.</param>
	/// <param name="failure">The failing rule whose remediation to apply.</param>
	/// <param name="onOutput">Where to say what happened.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <remarks>
	/// Through <see cref="VerifyWritableCloneAsync"/>, as every write to a clone must be. This
	/// replaced a public entry point that took a path and applied straight to it: a bulk rule fix fans
	/// out one item per repository, and for a repository that was never cloned that path wrote into a
	/// directory the remediation itself created — leaving files belonging to no repository, and a
	/// target <c>git clone</c> refuses for ever afterwards because it exists and is not empty.
	/// </remarks>
	public async Task<List<string>> ApplyRuleRemediationAsync(
		RepositoryDashboardRow row,
		RuleResult failure,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var applied = new List<string>();

		if (row.LocalPath is null)
		{
			onOutput?.Invoke($"⚠️ {row.RepositoryFullName} has no local path — cannot apply {failure.RuleId}.");
			return applied;
		}

		if (!await VerifyWritableCloneAsync(row, onOutput, cancellationToken).ConfigureAwait(false))
		{
			return applied;
		}

		ApplySingleRemediation(row.LocalPath, failure, applied, onOutput);
		return applied;
	}

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
		RepositoryDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return;
		}

		row.Status = PackageStatus.Building;
		row.StatusMessage = "Building...";

		var (success, _) = await _localRepo.BuildAsync(repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);

		row.Status = success ? PackageStatus.BuildSucceeded : PackageStatus.BuildFailed;
		row.StatusMessage = success ? "Build succeeded." : "Build failed.";
	}

	/// <summary>
	/// Syncs a local repository with remote (fetch, pull --rebase, push). Returns whether the clone
	/// ended up in step with origin.
	/// </summary>
	/// <remarks>
	/// The result matters to callers that write to the clone afterwards: a failure here can mean there
	/// is no clone at all — a clone into a directory that already exists and is not empty fails — and
	/// remediations create the directories they write into, so a caller that carries on regardless
	/// fills a bare directory with plausible-looking files belonging to no repository.
	/// </remarks>
	public async Task<bool> GitSyncAsync(
		RepositoryDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return false;
		}

		var isClonedLocally = _localRepo.IsClonedLocally(repoIdentity);
		if (!isClonedLocally)
		{
			var cloneUrl = BuildCloneUrl(row);
			if (cloneUrl is null)
			{
				row.Status = PackageStatus.Error;
				row.StatusMessage = "No repository known for this package.";
				onOutput?.Invoke($"❌ {row.RepositoryFullName}: no repository is known for this package, so there is nothing to clone.");
				return false;
			}

			onOutput?.Invoke($"Cloning {row.RepositoryFullName}...");
			var (cloneSuccess, cloneOutput) = await _localRepo.CloneAsync(cloneUrl, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);
			if (!cloneSuccess)
			{
				row.Status = PackageStatus.Error;
				row.StatusMessage = "Git sync failed.";
				onOutput?.Invoke(cloneOutput);
				return false;
			}

			row.IsClonedLocally = true;
			row.LocalPath = _localRepo.GetLocalPath(repoIdentity);
			onOutput?.Invoke($"Cloned to {row.LocalPath}");
		}
		else if (await VerifyLocalCloneAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false) is not null)
		{
			// An existing folder of the right name holding the wrong repository: syncing it would pull
			// somebody else's history into what this row believes is its checkout.
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Local folder holds a different repository.";
			return false;
		}

		row.Status = PackageStatus.GitSyncing;
		row.StatusMessage = "Syncing with remote...";

		var (success, _) = await _localRepo.GitSyncAsync(repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);

		await RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);

		row.Status = success ? PackageStatus.GitSynced : PackageStatus.Error;
		row.StatusMessage = success ? "Synced with remote." : "Git sync failed.";

		return success;
	}

	/// <summary>
	/// Commits all local changes, fetches, rebases on remote, and pushes.
	/// Does not change the row status (preserves current workflow state).
	/// </summary>
	public async Task<CommitAndPushOutcome> CommitAndPushAsync(
		RepositoryDashboardRow row,
		string commitMessage,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null)
		{
			var reason = $"No repository is known for {row.RepositoryFullName}, so there is nothing to push to.";
			onOutput?.Invoke($"❌ {reason}");
			return CommitAndPushOutcome.Refused(reason);
		}

		// The last line of defence, and the one that matters most: everything before this is local and
		// recoverable, whereas a push is not. Each guard's reason travels back so the caller can put it
		// in front of the user, rather than leaving it in a console they may not have open.
		var identityRefusal = await VerifyLocalCloneAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);
		if (identityRefusal is not null)
		{
			return CommitAndPushOutcome.Refused(identityRefusal);
		}

		var staleRefusal = await VerifyNotBehindOriginAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);
		if (staleRefusal is not null)
		{
			return CommitAndPushOutcome.Refused(staleRefusal);
		}

		var (success, _) = await _localRepo.CommitAndPushAsync(repoIdentity, commitMessage, onOutput, cancellationToken).ConfigureAwait(false);

		if (success)
		{
			// Refresh git status after push. The push itself establishes that we match origin, so no
			// fetch is needed to know it — but it is knowledge with an age, like any other.
			row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
			row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
			row.IsSyncedWithOrigin = true;
			row.SyncStatusCheckedAtUtc = DateTimeOffset.UtcNow;
		}

		return success ? CommitAndPushOutcome.Pushed : CommitAndPushOutcome.Failed;
	}

	// ── Repository identity: making sure we act on the repository we think we are ──

	/// <summary>
	/// The clone URL for a row, taken from the repository the row actually points at.
	/// </summary>
	/// <remarks>
	/// This used to be built from AppSettings:GitHubOrganization plus the repository name, which is
	/// wrong whenever a package's repository is not owned by the organisation it was discovered under —
	/// and that is not rare: a vendored or forked package is published by one organisation while its
	/// repository lives under another. Worse, it failed silently rather than loudly, because a
	/// same-named repository often does exist under the configured organisation, so the app would clone
	/// that one and push to it.
	/// </remarks>
	private static string? BuildCloneUrl(RepositoryDashboardRow row)
		=> row.RepositoryFullName is null
			? null
			: $"https://github.com/{row.RepositoryFullName}.git";

	/// <summary>
	/// The <c>owner/name</c> identity that locates this row's repository, both on GitHub and on disk.
	/// </summary>
	/// <remarks>
	/// Everything that touches the local clone goes through here. It used to derive a bare repository
	/// name from the URL and discard the owner, which is what let two organisations owning a same-named
	/// repository share one directory.
	/// </remarks>
	private static string? RepoIdentity(RepositoryDashboardRow row) => row.RepositoryFullName;

	/// <summary>
	/// Reduces a git remote URL to <c>owner/name</c>, lower-cased, so an https remote and an ssh one
	/// for the same repository compare equal.
	/// </summary>
	private static string? NormaliseRepoIdentity(string? remoteOrFullName)
	{
		if (string.IsNullOrWhiteSpace(remoteOrFullName))
		{
			return null;
		}

		var value = remoteOrFullName.Trim();

		var fromUrl = LocalRepoService.ParseRepoIdentity(value);
		if (fromUrl is not null)
		{
			return fromUrl.ToLowerInvariant();
		}

		// Not a URL. The two sides of the comparison are not the same shape: a clone's origin is a URL,
		// but a row carries its repository as a bare owner/name — so that form has to be accepted here,
		// or the guard rejects every row it is asked about and no fix is ever applied.
		var segments = value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		return segments.Length == 2 ? $"{segments[0]}/{segments[1]}".ToLowerInvariant() : null;
	}

	/// <summary>
	/// Confirms the local clone really is the repository this row refers to, before anything is written
	/// to it. Returns null when it is, or the reason when it is not — including when that cannot be
	/// established. The reason is both written to the output and handed back, so a caller can show it.
	/// </summary>
	/// <remarks>
	/// Paths are now qualified by owner, so this is no longer the only thing standing between a fix and
	/// the wrong repository — but a directory's name still does not prove what is checked out in it, and
	/// the operations that follow edit files, commit and push. An unverifiable remote is therefore
	/// treated as a mismatch: refusing to act is recoverable, pushing to the wrong repository is not.
	/// </remarks>
	private async Task<string?> VerifyLocalCloneAsync(
		RepositoryDashboardRow row,
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var expected = NormaliseRepoIdentity(row.RepositoryFullName);
		if (expected is null)
		{
			return Refuse($"No repository is known for {row.RepositoryFullName}, so nothing can be applied to it.", onOutput);
		}

		var origin = await _localRepo.GetOriginUrlAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		var actual = NormaliseRepoIdentity(origin);

		if (actual is null)
		{
			return Refuse(
				$"The origin remote of {_localRepo.GetLocalPath(repoIdentity)} cannot be read, so it cannot be confirmed as {row.RepositoryFullName}. Refusing to write to it.",
				onOutput);
		}

		if (!string.Equals(actual, expected, StringComparison.Ordinal))
		{
			return Refuse(
				$"{_localRepo.GetLocalPath(repoIdentity)} is a clone of {actual}, not {expected}. Refusing to write to the wrong repository.",
				onOutput);
		}

		return null;
	}

	/// <summary>
	/// Reports a guard's refusal to the output and returns it, so the one wording serves both.
	/// </summary>
	private static string Refuse(string reason, Action<string>? onOutput)
	{
		onOutput?.Invoke($"❌ {reason}");
		return reason;
	}

	/// <summary>
	/// Confirms the clone has no uncommitted changes before the app starts making its own. Returns
	/// false — and says why — when it does, or when the state cannot be established.
	/// </summary>
	/// <remarks>
	/// The commit that follows a remediation stages with <c>git add -A</c>, so anything already modified
	/// in the working tree would be swept into it and pushed. Nothing the app does needs to begin from a
	/// dirty tree, so an unexplained change is treated as a reason to stop: whatever it is, it did not
	/// come from here, and committing it under a governance message would misattribute it. An
	/// indeterminate answer fails closed for the same reason the identity check does.
	/// </remarks>
	private async Task<bool> VerifyCleanWorkingTreeAsync(
		RepositoryDashboardRow row,
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var isClean = await _localRepo.IsWorkingTreeCleanAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.IsWorkingTreeClean = isClean;

		if (isClean == true)
		{
			return true;
		}

		if (isClean is null)
		{
			onOutput?.Invoke($"❌ {row.RepositoryFullName}: cannot determine whether {_localRepo.GetLocalPath(repoIdentity)} has uncommitted changes. Refusing to write to it.");
			return false;
		}

		var preview = await _localRepo
			.GetWorkingTreeStatusPreviewAsync(repoIdentity, 3, cancellationToken)
			.ConfigureAwait(false);

		// Deliberately says nothing about whose changes these are, because it cannot tell: a fix applied
		// and not yet committed looks the same as somebody else's work in progress. Either way the answer
		// is the same, so the message says what to do rather than guessing at what happened.
		onOutput?.Invoke(
			$"❌ {row.RepositoryFullName}: {_localRepo.GetLocalPath(repoIdentity)} has uncommitted changes, which a governance commit would stage and push along with its own. Commit or discard them first.");

		foreach (var line in preview)
		{
			onOutput?.Invoke($"    {line}");
		}

		return false;
	}

	/// <summary>
	/// Confirms origin has not moved on since the changes about to be committed were decided. Returns
	/// false, having said why, when it has — or when that cannot be established.
	/// </summary>
	/// <remarks>
	/// This is where staleness is caught, rather than before each fix or prompt, and deliberately so.
	/// Applying a fix edits files in a clone of the app's own and generating a prompt produces text: both
	/// are undoable and invisible to anybody else, so checking them would add friction in several places
	/// to prevent damage that can only happen in one. A push is the single step that cannot be taken back.
	///
	/// What it prevents specifically: a fix decided from an assessment of an older tree being committed on
	/// top of work pushed since — which at best duplicates a fix somebody already made, and at worst
	/// commits a change whose reason no longer holds. Left to itself, the commit would have rebased onto
	/// that newer work and pushed regardless, quietly.
	/// </remarks>
	private async Task<string?> VerifyNotBehindOriginAsync(
		RepositoryDashboardRow row,
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var behind = await _localRepo.CountCommitsBehindOriginAsync(repoIdentity, cancellationToken).ConfigureAwait(false);

		if (behind is null)
		{
			// Fails closed, for the same reason the identity check does: not knowing is not a licence.
			return Refuse(
				$"Whether origin has moved on cannot be established for {row.RepositoryFullName}, so nothing was pushed. Check the repository can reach origin, then Sync.",
				onOutput);
		}

		if (behind > 0)
		{
			row.IsSyncedWithOrigin = false;
			row.SyncStatusCheckedAtUtc = DateTimeOffset.UtcNow;

			var reason =
				$"Origin has {behind} commit(s) that {row.RepositoryFullName}'s clone does not, added since these changes were worked out. "
				+ "Nothing was pushed, because the changes were decided against a repository that has moved on.";

			// Discarded rather than left for the user to deal with: they were derived from an assessment of
			// a tree that has been superseded, so they cannot be salvaged into something correct — and
			// leaving them would only block the next fix on the clean-tree gate. Re-applying after a sync
			// is cheap, and may turn out to be unnecessary.
			var (discardSuccess, discarded) = await _localRepo
				.DiscardLocalChangesAsync(repoIdentity, cancellationToken)
				.ConfigureAwait(false);

			if (discardSuccess)
			{
				row.IsWorkingTreeClean = true;
				reason += $"\n\nThe {discarded.Count} local change(s) have been discarded. Sync, then apply the fix again — it may no longer be needed.";

				onOutput?.Invoke($"❌ {reason}");
				foreach (var line in discarded)
				{
					onOutput?.Invoke($"   ↩️ discarded {line}");
				}

				return reason;
			}

			reason += "\n\nThe local changes could not be discarded, so Sync will need them cleared first.";
			return Refuse(reason, onOutput);
		}

		return null;
	}

	/// <summary>
	/// The single gate every write to a local clone passes through: it is the repository we think it is,
	/// and it has nothing uncommitted in it. Returns false — having said why — if either does not hold.
	/// </summary>
	private async Task<bool> VerifyWritableCloneAsync(
		RepositoryDashboardRow row,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null)
		{
			onOutput?.Invoke($"❌ {row.RepositoryFullName}: no repository is known for this package, so nothing can be applied to it.");
			return false;
		}

		return await VerifyLocalCloneAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false) is null
			&& await VerifyCleanWorkingTreeAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);
	}

	// ── Issue-centric ("dimensional flip") view + bulk apply across repositories ──

	/// <summary>
	/// Builds the issue-centric view (Category → Rule → Repository) from the assessed rows,
	/// marking each occurrence's auto-remediability via the remediation registry.
	/// </summary>
	public IssueCentricView BuildIssueCentricView(IEnumerable<RepositoryDashboardRow> rows)
	{
		// Pass the package id through: a repository hosting several packages appears once per package
		// under a rule, and this is what tells those occurrences apart in the issue tree.
		//
		// Augmented so that open Dependabot pull requests appear here too, one node per dependency —
		// otherwise a backlog spread across the estate can only be cleared a repository at a time.
		var entries = rows
			.Where(r => r.RepositoryFullName is not null && r.Assessment is not null)
			.Select(r => new AssessedPackage(
				r.RepositoryFullName,
				DependabotIssueSynthesizer.Augment(r),
				r.RepositoryFullName));
		return IssueCentricViewBuilder.Build(entries, IsAutoRemediable);
	}

	/// <summary>
	/// Generates a single consolidated AI prompt for one issue class across every affected repository.
	/// </summary>
	public string GenerateCombinedRulePrompt(IEnumerable<RepositoryDashboardRow> rows, string ruleId)
	{
		var issueClass = BuildIssueCentricView(rows).AllIssueClasses
			.FirstOrDefault(i => string.Equals(i.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
		return issueClass is null ? string.Empty : CombinedRemediationPromptBuilder.ForRule(issueClass);
	}

	/// <summary>
	/// Generates a single consolidated AI prompt for a category across every affected repository.
	/// </summary>
	public string GenerateCombinedCategoryPrompt(IEnumerable<RepositoryDashboardRow> rows, AssessmentCategory category, bool onlyNonRemediable = true)
	{
		var group = BuildIssueCentricView(rows).Categories.FirstOrDefault(c => c.Category == category);
		return group is null ? string.Empty : CombinedRemediationPromptBuilder.ForCategory(group, onlyNonRemediable);
	}

	// ApplyRuleAcrossReposAsync, ApplyCategoryAcrossReposAsync and ApplyEverythingAcrossReposAsync used
	// to live here — IssuesView's bulk actions called them directly, sequentially, one repository at a
	// time. They are gone: a bulk action is now fanned out onto per-repository lanes (see
	// IssuesView.RunConfirmedAsync and WorkFanOut.EnqueueRule), so nothing calls them any more.
	// ApplyAcrossReposAsync (below), ApplySingleRuleAsync, RepoHasFailingRule and RepoHasFailingCategory
	// are left in place despite now having no callers either: removing them cascades into
	// BulkApplyOutcome/RepoApplyResult/RepoApplyPhase/RepoApplyStatus becoming unused too, which is a
	// separate call to make.

	private Task<List<string>> ApplySingleRuleAsync(RepositoryDashboardRow row, string ruleId, Action<string>? onOutput)
	{
		var applied = new List<string>();
		var fresh = row.Assessment?.RuleResults
			.FirstOrDefault(rr => !rr.Passed && string.Equals(rr.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
		if (fresh is not null && row.LocalPath is not null && IsAutoRemediable(fresh))
		{
			ApplySingleRemediation(row.LocalPath, fresh, applied, onOutput);
		}

		return Task.FromResult(applied);
	}

	/// <summary>
	/// Throws away everything a stopped run had written into a clone, and says what went. Never
	/// cancellable: this is the cleanup, and abandoning it half-way is the state it exists to prevent.
	/// </summary>
	private async Task RevertUncommittedAsync(RepositoryDashboardRow row, string name, Action<string>? onOutput)
	{
		var identity = row.RepositoryFullName;
		if (identity is null)
		{
			return;
		}

		var (success, discarded) = await _localRepo.DiscardLocalChangesAsync(identity, CancellationToken.None)
			.ConfigureAwait(false);

		if (!success)
		{
			onOutput?.Invoke($"⚠️ {name}: could not revert the part-applied changes — check the clone by hand.");
			return;
		}

		// Announced rather than silent: a rollback the user cannot see is worse than none.
		onOutput?.Invoke(discarded.Count == 0
			? $"↩️ {name}: stopped before anything was written."
			: $"↩️ {name}: reverted {discarded.Count} change(s) written before the stop.");
	}

	private async Task<BulkApplyOutcome> ApplyAcrossReposAsync(
		List<RepositoryDashboardRow> affected,
		Func<RepositoryDashboardRow, Task<List<string>>> applyFunc,
		string commitMessage,
		Action<string>? onOutput,
		IProgress<string>? onProgress,
		Action<RepositoryDashboardRow>? onRepositoryFixed,
		CancellationToken cancellationToken)
	{
		var outcome = new BulkApplyOutcome();
		onOutput?.Invoke($"Applying across {affected.Count} repositor{(affected.Count == 1 ? "y" : "ies")}...");

		var index = 0;
		foreach (var row in affected)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = row.RepositoryFullName;
			onOutput?.Invoke($"── {name} ──");

			// Two channels, deliberately: the console gets the narrative, the queue entry gets a count
			// short enough to sit in the sidebar next to the title.
			onProgress?.Report($"repo {++index} of {affected.Count}");

			// Tracks the atomic boundary for this repository: everything before the commit can be
			// undone, everything after it has left the machine.
			var phase = RepoApplyPhase.NotStarted;

			try
			{
				// Checked before the sync rather than after it: a rebase onto an already-dirty tree is
				// its own hazard, and skipping the repository entirely is the safe outcome either way.
				if (row.IsClonedLocally && !await VerifyWritableCloneAsync(row, onOutput, cancellationToken).ConfigureAwait(false))
				{
					outcome.Results.Add(new RepoApplyResult
					{
						RepositoryFullName = name,
						Status = RepoApplyStatus.Skipped,
						Message = "Skipped: the local clone is not in a state it is safe to write to."
					});
					continue;
				}

				// Guardrail: never commit onto a stale clone — sync, then reassess the fresh tree. The
				// sync is also what clones a repository being remediated for the first time, so its
				// failure has to stop the repository here: carrying on would apply the remediation to
				// whatever the path happens to be, up to and including a directory that is not a clone.
				if (!await GitSyncAsync(row, onOutput, cancellationToken).ConfigureAwait(false))
				{
					outcome.Results.Add(new RepoApplyResult
					{
						RepositoryFullName = name,
						Status = RepoApplyStatus.Skipped,
						Message = "Skipped: the local clone could not be synced with origin."
					});
					continue;
				}

				// The gate again, now unconditionally: the check above is skipped for a row with no
				// clone, and the sync was supposed to produce one. Remediations create the directories
				// they write into, so without this a bare directory gets filled with files belonging to
				// no repository, and nothing notices until the push refuses to touch it.
				if (!await VerifyWritableCloneAsync(row, onOutput, cancellationToken).ConfigureAwait(false))
				{
					outcome.Results.Add(new RepoApplyResult
					{
						RepositoryFullName = name,
						Status = RepoApplyStatus.Skipped,
						Message = "Skipped: the local clone is not in a state it is safe to write to."
					});
					continue;
				}

				await AssessLocalRepositoryAsync(row, cancellationToken).ConfigureAwait(false);

				phase = RepoApplyPhase.Applying;
				var applied = await applyFunc(row).ConfigureAwait(false);
				phase = applied.Count == 0 ? RepoApplyPhase.NotStarted : RepoApplyPhase.Applied;
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

				// Verify before pushing. The regression guard can only revert *after* a push, which
				// leaves a live branch broken in the meantime - and it cannot attribute the breakage
				// at all when a remediation has broken the toolchain, because the control build fails
				// too. Building here makes a bad remediation cost a skipped repository instead.
				onOutput?.Invoke($"🔎 Verifying {name} builds before pushing...");
				var preflight = await _localRepo.BuildWithRestoreAsync(name, onOutput, cancellationToken).ConfigureAwait(false);
				if (!preflight.Success)
				{
					// Same principle as the cancellation path: work that never reached a commit is
					// undone, so the clone is left as it was found rather than half-remediated.
					await RevertUncommittedAsync(row, name, onOutput).ConfigureAwait(false);
					phase = RepoApplyPhase.NotStarted;
					outcome.Results.Add(new RepoApplyResult
					{
						RepositoryFullName = name,
						// A refusal is a skip, not a failure: nothing was left behind.
						Status = RepoApplyStatus.Skipped,
						Message = "Not pushed: the repository does not build after applying the remediation. "
							+ "The change was reverted locally. This is a bug in the remediation."
					});
					onOutput?.Invoke($"⛔ {name}: does not build after applying - not pushed, change reverted.");
					continue;
				}

				// Deliberately not cancellable: a push killed mid-ref-update is the one outcome that
				// cannot be tidied up afterwards. Once it starts, it is allowed to resolve, and the
				// stop is honoured at the top of the next repository.
				var push = await CommitAndPushAsync(row, commitMessage, onOutput, CancellationToken.None).ConfigureAwait(false);
				phase = push.Success ? RepoApplyPhase.Pushed : RepoApplyPhase.Applied;
				outcome.Results.Add(new RepoApplyResult
				{
					RepositoryFullName = name,
					// A refusal is a skip, not a failure: a guard decided, and nothing was left behind.
					Status = push.Success
						? RepoApplyStatus.Pushed
						: push.WasRefused ? RepoApplyStatus.Skipped : RepoApplyStatus.Failed,
					Message = push.Success
						? $"{applied.Count} file(s) committed & pushed."
						: push.RefusalReason ?? "Commit/push failed."
				});

				if (push.Success)
				{
					// Re-assessed now the fix has landed, so the row stops reporting an issue that no
					// longer exists. Without this the issue tree still shows the failures a run has just
					// fixed, because everything downstream reads the assessment taken before the change.
					await AssessLocalRepositoryAsync(row, cancellationToken).ConfigureAwait(false);
					onRepositoryFixed?.Invoke(row);

					// Verify the build in the background; auto-revert if our change broke it.
					_regressionGuard.Enqueue(row.RepositoryFullName);
					onOutput?.Invoke($"🛡️ Queued {row.RepositoryFullName} for build verification.");
				}

				if (!push.Success)
				{
					outcome.StoppedOnFailure = true;
					onOutput?.Invoke($"⛔ Stopping bulk apply: {(push.WasRefused ? "a guard refused the push" : "commit/push failed")}.");
					break;
				}
			}
			catch (OperationCanceledException)
			{
				// Atomic per repository: work that never reached its commit is undone, so the clone is
				// left as it was found rather than half-remediated.
				if (BulkApplyCancellation.NeedsRevert(phase))
				{
					await RevertUncommittedAsync(row, name, onOutput).ConfigureAwait(false);
				}

				outcome.Results.Add(BulkApplyCancellation.Describe(name, phase));
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

	private static bool RepoHasFailingRule(RepositoryDashboardRow row, string ruleId)
		=> row.Assessment?.RuleResults.Any(rr => !rr.Passed && string.Equals(rr.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)) == true;

	private static bool RepoHasFailingCategory(RepositoryDashboardRow row, AssessmentCategory category)
		=> row.Assessment?.RuleResults.Any(rr => !rr.Passed && rr.Category == category) == true;

	/// <summary>
	/// Refreshes the git status for a row (branch, working tree clean state, and sync status with origin).
	/// </summary>
	public async Task RefreshGitStatusAsync(RepositoryDashboardRow row, CancellationToken cancellationToken = default)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null || !row.IsClonedLocally)
		{
			return;
		}

		row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.IsSyncedWithOrigin = await _localRepo.IsSyncedWithOriginAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.SyncStatusCheckedAtUtc = DateTimeOffset.UtcNow;
		row.LatestTag = await _localRepo.GetLatestTagAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Refreshes only the git facts that can be read without contacting the remote: the current branch
	/// and whether the working tree is clean.
	/// </summary>
	/// <remarks>
	/// Separate from <see cref="RefreshGitStatusAsync"/> because that one compares against origin, which
	/// costs a fetch — too much to pay every time a repository is clicked. These two are local reads, and
	/// they are the ones that go stale behind the app's back: anything done to a checkout outside the
	/// dashboard leaves the row asserting a branch and a cleanliness that were true when it last acted.
	/// The sync comparison is deliberately left alone rather than invalidated, so what it last established
	/// stays on screen with its age (see <see cref="RepositoryDashboardRow.SyncStatusCheckedAtUtc"/>).
	/// </remarks>
	public async Task RefreshLocalGitStatusAsync(RepositoryDashboardRow row, CancellationToken cancellationToken = default)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null || !row.IsClonedLocally)
		{
			return;
		}

		row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Returns a short preview of dirty working tree lines for diagnostics in UI output.
	/// </summary>
	public async Task<IReadOnlyList<string>> GetWorkingTreeStatusPreviewAsync(
		RepositoryDashboardRow row,
		int maxLines = 3,
		CancellationToken cancellationToken = default)
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
	public async Task RunTestsAsync(
		RepositoryDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
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

		var (success, _) = await _localRepo.RunTestsAsync(repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);

		row.Status = success ? PackageStatus.TestsPassed : PackageStatus.TestsFailed;
		row.StatusMessage = success ? "All tests passed." : "Tests failed.";
	}

	/// <summary>
	/// Runs the publish script on a local repository.
	/// </summary>
	public async Task RunPublishAsync(
		RepositoryDashboardRow row,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
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

}
