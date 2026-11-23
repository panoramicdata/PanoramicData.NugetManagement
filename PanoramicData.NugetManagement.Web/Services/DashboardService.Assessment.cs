using System.Globalization;
using Microsoft.Extensions.Options;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class DashboardService
{
	/// <summary>
	/// The per-repository assessment options, as configured for this installation.
	/// </summary>
	/// <remarks>
	/// One place, because three callers now need the same answer — the two assessment paths and
	/// Dependabot triage — and an option added to one of them but not the others would assess the
	/// same repository differently depending on which path reached it.
	/// </remarks>
	/// <param name="repositoryFullName">
	/// The repository the options are for, as "owner/name". Its waivers are read from the estate's
	/// committed catalogue and its licence from the overrides in runtime settings. Required rather than
	/// defaulted, so a new caller has to decide which repository it is asking about instead of
	/// silently getting one that is held to every rule.
	/// </param>
	private RepoOptions BuildRepoOptions(string? repositoryFullName)
	{
		var license = _runtimeSettings.ResolveLicense(repositoryFullName);
		var repoOptions = new RepoOptions
		{
			ExpectedLicense = license.License,
			ExpectedLicenseFileText = license.LicenseFileText,
			ExpectedCopyrightHolder = _settings.CopyrightHolder,
			ExpertiseBannerTemplate = string.IsNullOrWhiteSpace(_settings.ExpertiseBannerTemplate)
				? Standards.ExpertiseBannerTemplate
				: _settings.ExpertiseBannerTemplate,
			NuGetUser = _settings.NuGetUser,
			Waivers = [.. _waivers.For(repositoryFullName)],
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

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _privateRepositories = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Whether a repository is private, or null when GitHub cannot say; asked once per repository per run.</summary>
	private static async Task<bool?> ResolveIsPrivateAsync(IGitHubClient? github, string repositoryFullName)
	{
		if (_privateRepositories.TryGetValue(repositoryFullName, out var known))
		{
			return known;
		}

		var parts = repositoryFullName.Split('/');
		if (github is null || parts.Length != 2)
		{
			return null;
		}

		try
		{
			var repository = await github.Repository.Get(parts[0], parts[1]).ConfigureAwait(false);
			_privateRepositories[repositoryFullName] = repository.Private;
			return repository.Private;
		}
		catch (Exception ex) when (ex is ApiException or HttpRequestException)
		{
			return null;
		}
	}

	/// <summary>Whether private vulnerability reporting is on for a repository, or null when unknown.</summary>
	private static async Task<bool?> ResolvePrivateVulnerabilityReportingAsync(IGitHubClient? github, string repositoryFullName)
	{
		var parts = repositoryFullName.Split('/');
		return github is null || parts.Length != 2
			? null
			: await RepositoryContextBuilder.ResolvePrivateVulnerabilityReportingAsync(github, parts[0], parts[1]).ConfigureAwait(false);
	}

	/// <summary>The line coverage Codacy holds for a repository, or null when it cannot be read.</summary>
	/// <remarks>The local clone cannot know this, so without it every locally assessed repository grades RED on TST-10.</remarks>
	private static async Task<double?> ResolveLineCoverageAsync(
		string repositoryFullName,
		RepoOptions repoOptions,
		string branch,
		CancellationToken cancellationToken)
	{
		var parts = repositoryFullName.Split('/');
		return parts.Length != 2
			? null
			: await RepositoryContextBuilder.ResolveCoverageAsync(
				new CodacyCoverageService(),
				repoOptions.Codacy?.ApiToken,
				parts[0],
				parts[1],
				branch,
				cancellationToken).ConfigureAwait(false);
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
		CancellationToken cancellationToken)
	{
		var repoOptions = BuildRepoOptions(row.RepositoryFullName);

		using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());

		if (row is { IsClonedLocally: true, LocalPath: not null })
		{
			return new LocalRepositoryContextBuilder(
					loggerFactory.CreateLogger<LocalRepositoryContextBuilder>())
				.Build(
					row.LocalPath,
					row.RepositoryFullName,
					repoOptions,
					new LocalContextFacts
					{
						DefaultBranch = "main",
						CurrentBranch = row.CurrentBranch,
						LatestTag = row.LatestTag,
						LatestPublishedVersion = row.PrimaryPackage?.LatestVersion,
						ReleaseRun = row.ReleaseRun,
						HeadSha = await _localRepo.GetHeadShaAsync(row.RepositoryFullName, cancellationToken).ConfigureAwait(false),
						NextVersion = row.NextVersion,
						IsConfirmedInSyncWithOrigin = RepositoryGitState.IsRecentlyConfirmedInSync(row, DateTimeOffset.UtcNow),
						LineCoveragePercent = await ResolveLineCoverageAsync(row.RepositoryFullName, repoOptions, "main", cancellationToken).ConfigureAwait(false),
						PrivateVulnerabilityReportingEnabled = await ResolvePrivateVulnerabilityReportingAsync(github, row.RepositoryFullName).ConfigureAwait(false),
						IsPrivateRepository = await ResolveIsPrivateAsync(github, row.RepositoryFullName).ConfigureAwait(false)
					});
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
		CancellationToken cancellationToken)
	{
		if (row.RepositoryFullName is null)
		{
			FailAssessment(row, "No repository identified.");
			return;
		}

		row.Status = PackageStatus.Assessing;
		row.StatusMessage = "Assessing...";

		try
		{
			await AssessViaGitHubAsync(row, github, cancellationToken).ConfigureAwait(false);
		}
		catch (NotFoundException)
		{
			_logger.LogWarning("Repository {Repo} not found on GitHub (private, renamed, or wrong owner).", row.RepositoryFullName);
			FailAssessment(row, $"Repository '{row.RepositoryFullName}' not found on GitHub (private, renamed, or wrong owner).");
		}
		catch (RateLimitExceededException)
		{
			_logger.LogWarning("GitHub rate limit hit while assessing {Repo}.", row.RepositoryFullName);
			FailAssessment(row, "GitHub rate limit reached. Configure a Personal Access Token (AppSettings:GitHubPat) or wait a few minutes.");
		}
		catch (ApiException ex) when (IsSecondaryRateLimit(ex))
		{
			_logger.LogWarning("GitHub secondary rate limit hit while assessing {Repo}.", row.RepositoryFullName);
			FailAssessment(row, "GitHub secondary rate limit reached. Configure a Personal Access Token (AppSettings:GitHubPat) or wait a few minutes.");
		}
		catch (AuthorizationException)
		{
			_logger.LogWarning("GitHub authorization failed while assessing {Repo}.", row.RepositoryFullName);
			FailAssessment(row, "GitHub authentication failed. Check the token is valid and has 'repo' + 'read:org' scopes.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to assess {Repo}", row.RepositoryFullName);
			FailAssessment(row, $"Assessment failed: {ex.Message}");
		}
	}

	private async Task AssessViaGitHubAsync(RepositoryDashboardRow row, IGitHubClient github, CancellationToken cancellationToken)
	{
		// Read what is actually on nuget.org now. Discovery is the only other thing that writes
		// this, and Re-assess skips discovery, so without this CI-11 compares a fresh tag against
		// an hours-old version and cannot be talked out of it.
		await _publishedVersions.RefreshAsync(row, cancellationToken).ConfigureAwait(false);

		var parts = row.RepositoryFullName!.Split('/');
		if (parts.Length != 2)
		{
			FailAssessment(row, "Invalid repository full name.");
			return;
		}

		var repo = await github.Repository.Get(parts[0], parts[1]).ConfigureAwait(false);
		var repoOptions = BuildRepoOptions(row.RepositoryFullName);

		using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
		using var contextBuilder = new RepositoryContextBuilder(github, loggerFactory.CreateLogger<RepositoryContextBuilder>());
		var context = await contextBuilder.BuildAsync(repo, repoOptions, cancellationToken).ConfigureAwait(false);

		var results = await EvaluateRulesAsync(context, repoOptions, cancellationToken).ConfigureAwait(false);
		StoreAssessment(row, context, results);

		await RefreshOpenIssuesAsync(row, github, parts[0], parts[1], cancellationToken).ConfigureAwait(false);
		PreviewTriage(row, context, results);

		row.Status = PackageStatus.Assessed;
		row.StatusMessage = $"{row.TotalFailures} issue(s) found.";
	}

	private static void FailAssessment(RepositoryDashboardRow row, string message)
	{
		row.Status = PackageStatus.Error;
		row.StatusMessage = message;
	}

	private static bool IsSecondaryRateLimit(ApiException ex)
		=> ex.Message.Contains("abuse", StringComparison.OrdinalIgnoreCase)
			|| ex.Message.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase);

	private static void StoreAssessment(RepositoryDashboardRow row, RepositoryContext context, List<RuleResult> results)
	{
		row.Assessment = new RepoAssessment
		{
			RepositoryFullName = row.RepositoryFullName,
			DefaultBranch = context.DefaultBranch,
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = results
		};

		row.CategorySummaries = BuildCategorySummaries(results);
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
	/// Works out what a triage pass would conclude about each open pull request, and records it on the
	/// row, without touching GitHub.
	/// </summary>
	/// <param name="row">The row whose open items have just been refreshed.</param>
	/// <param name="context">The repository, as the assessment just built it.</param>
	/// <param name="results">The assessment those verdicts are judged against.</param>
	/// <remarks>
	/// A verdict used to exist only after Fix had run, because
	/// <see cref="DependabotTriageRunner.RunAsync"/> was the one place that wrote it — and that method
	/// comments on and closes pull requests as it goes. So the only way to find out whether a pull
	/// request was already covered by a failing rule was to authorise the writes, and until then the
	/// tree said "Not triaged" about everything. <see cref="DependabotTriageService"/> is pure, so
	/// there was never a reason for the answer to cost a mutation.
	/// <para>
	/// Read-only, deliberately and in every sense: no write API, no adopter, and nothing closed. It
	/// predicts the pass rather than performing part of it — an adoption written here would leave the
	/// clone changed by something the user only asked to re-assess.
	/// </para>
	/// <para>
	/// A failure here must not fail the assessment. The rules have already been evaluated by the time
	/// this runs, and losing them because a scanner threw on a malformed project file would be the same
	/// poor trade <see cref="RefreshOpenIssuesAsync"/> declines to make.
	/// </para>
	/// </remarks>
	private void PreviewTriage(
		RepositoryDashboardRow row,
		RepositoryContext context,
		IReadOnlyList<RuleResult> results)
	{
		if (!row.OpenIssuesKnown)
		{
			return;
		}

		var pullRequests = row.OpenIssues.Where(issue => issue.IsPullRequest).ToList();

		if (pullRequests.Count == 0)
		{
			return;
		}

		try
		{
			DependabotTriageRunner.Preview(
				pullRequests,
				new DependabotTriageService().Triage(
					pullRequests,
					context,
					results,
					ruleId => RemediationRegistry.Get(ruleId) is not null));
		}
		catch (Exception ex)
		{
			_logger.LogWarning(
				ex,
				"Could not predict triage verdicts for {Repo}; its pull requests will show as untriaged.",
				row.RepositoryFullName);
		}
	}

	/// <summary>
	/// Reads the CI run for the row's newest tag, so CI-11 can tell a release in flight from one that
	/// failed and CI-13 can report the failure.
	/// </summary>
	/// <remarks>
	/// Only the local assess path needs this: the remote path builds its context from the GitHub tree
	/// and supplies no tag at all, so both rules are not applicable there. With no client the run is
	/// cleared rather than left as it was — an unrefreshed run is stale evidence about a release that
	/// may since have finished, and clearing it returns both rules to the behaviour they had before
	/// runs were read at all.
	/// </remarks>
	private async Task RefreshReleaseRunAsync(
		RepositoryDashboardRow row,
		IGitHubClient? github,
		CancellationToken cancellationToken)
	{
		if (github is null)
		{
			row.ReleaseRun = null;
			return;
		}

		using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());

		await new ReleaseRunRefresher(
				new ReleaseRunService(github, loggerFactory.CreateLogger<ReleaseRunService>()))
			.RefreshAsync(row, cancellationToken)
			.ConfigureAwait(false);
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
		CancellationToken cancellationToken,
		IGitHubClient? github)
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
			await RefreshLocalGitFactsAsync(row, repoIdentity, cancellationToken).ConfigureAwait(false);

			// After the tag is known, because the run is looked up by it.
			await RefreshReleaseRunAsync(row, github, cancellationToken).ConfigureAwait(false);

			var defaultBranch = await ResolveDefaultBranchAsync(row, repoIdentity, cancellationToken).ConfigureAwait(false);

			var repoOptions = BuildRepoOptions(row.RepositoryFullName);

			var context = await BuildLocalContextAsync(row, repoIdentity, repoOptions, defaultBranch, github, cancellationToken).ConfigureAwait(false);

			var results = await EvaluateRulesAsync(context, repoOptions, cancellationToken).ConfigureAwait(false);

			StoreAssessment(row, context, results);

			var inboxWarning = await RefreshInboxIfPossibleAsync(row, github, cancellationToken).ConfigureAwait(false);

			PreviewTriage(row, context, results);

			row.Status = PackageStatus.Assessed;
			row.StatusMessage = $"{row.TotalFailures} issue(s) found (local)." + (inboxWarning is null ? string.Empty : $" {inboxWarning}");
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

	private async Task RefreshLocalGitFactsAsync(RepositoryDashboardRow row, string? repoIdentity, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(repoIdentity))
		{
			return;
		}

		row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);

		// Read here rather than relying on a git-status refresh having happened: CI-11 compares
		// this with what is on nuget.org, and without it the rule has nothing to say. A local
		// `git describe` on a clone we already have is cheap.
		row.LatestTag = await _localRepo.GetLatestTagAsync(repoIdentity, cancellationToken).ConfigureAwait(false);

		// What a release from this clone would publish, for VER-05 to compare with the tag.
		row.NextVersion = await _localRepo.GetNextVersionAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
	}

	private async Task<string> ResolveDefaultBranchAsync(RepositoryDashboardRow row, string? repoIdentity, CancellationToken cancellationToken)
	{
		var detectedDefaultBranch = !string.IsNullOrWhiteSpace(repoIdentity)
			? await _localRepo.GetRemoteDefaultBranchAsync(repoIdentity!, cancellationToken).ConfigureAwait(false)
			: null;

		return string.IsNullOrWhiteSpace(detectedDefaultBranch)
			? row.Assessment?.DefaultBranch ?? "main"
			: detectedDefaultBranch;
	}

	private async Task<RepositoryContext> BuildLocalContextAsync(
		RepositoryDashboardRow row,
		string? repoIdentity,
		RepoOptions repoOptions,
		string defaultBranch,
		IGitHubClient? github,
		CancellationToken cancellationToken)
	{
		using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
		var localBuilder = new LocalRepositoryContextBuilder(loggerFactory.CreateLogger<LocalRepositoryContextBuilder>());
		// The release facts the dashboard already knows, handed to the rules so CI-11 can compare
		// what was tagged with what was actually published.
		// The checked-out commit, so CQ-06 can say whether Codacy's grades describe it or an older
		// one. Null when git cannot be read, which leaves that comparison unmade.
		var headSha = !string.IsNullOrWhiteSpace(repoIdentity)
			? await _localRepo.GetHeadShaAsync(repoIdentity!, cancellationToken).ConfigureAwait(false)
			: null;

		return localBuilder.Build(
			row.LocalPath!,
			row.RepositoryFullName!,
			repoOptions,
			new LocalContextFacts
			{
				DefaultBranch = defaultBranch,
				CurrentBranch = row.CurrentBranch,
				LatestTag = row.LatestTag,
				LatestPublishedVersion = row.PrimaryPackage?.LatestVersion,
				ReleaseRun = row.ReleaseRun,
				HeadSha = headSha,
				NextVersion = row.NextVersion,
				IsConfirmedInSyncWithOrigin = RepositoryGitState.IsRecentlyConfirmedInSync(row, DateTimeOffset.UtcNow),
				LineCoveragePercent = await ResolveLineCoverageAsync(row.RepositoryFullName!, repoOptions, defaultBranch, cancellationToken).ConfigureAwait(false),
				PrivateVulnerabilityReportingEnabled = await ResolvePrivateVulnerabilityReportingAsync(github, row.RepositoryFullName!).ConfigureAwait(false),
				IsPrivateRepository = await ResolveIsPrivateAsync(github, row.RepositoryFullName!).ConfigureAwait(false)
			});
	}

	private static async Task<List<RuleResult>> EvaluateRulesAsync(RepositoryContext context, RepoOptions repoOptions, CancellationToken cancellationToken)
	{
		var results = new List<RuleResult>();

		foreach (var rule in RuleRegistry.Rules)
		{
			// Waived rules are evaluated like any other and excused afterwards, so a waiver that has
			// outlived the problem it was agreed for can be reported as stale rather than standing
			// unexamined for ever.
			var result = await rule.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
			results.Add(RuleWaivers.Apply(result, repoOptions));
		}

		return results;
	}

	/// <summary>
	/// Reads the inbox for a local assessment. A rate limit, secondary limit or authorisation failure
	/// here must not discard rules that have already been evaluated, so it is returned as a warning
	/// and the inbox is left unknown (grey) instead.
	/// </summary>
	internal async Task<string?> RefreshInboxIfPossibleAsync(RepositoryDashboardRow row, IGitHubClient? github, CancellationToken cancellationToken)
	{
		// The local path is the one every actively-worked repository takes, so without this it
		// would be exactly those repositories whose inbox was never read.
		if (github is null)
		{
			return null;
		}

		var identityParts = row.RepositoryFullName!.Split('/');
		if (identityParts.Length != 2)
		{
			return null;
		}

		try
		{
			await RefreshOpenIssuesAsync(row, github, identityParts[0], identityParts[1], cancellationToken)
				.ConfigureAwait(false);
			return null;
		}
		catch (Exception ex) when (ex is RateLimitExceededException or AuthorizationException
			|| (ex is ApiException api && IsSecondaryRateLimit(api)))
		{
			_logger.LogWarning(ex, "Could not refresh the inbox for {Repo}; it will show as unknown.", row.RepositoryFullName);
			row.OpenIssuesKnown = false;
			return ex is AuthorizationException
				? "(Inbox not refreshed: GitHub authentication failed.)"
				: "(Inbox not refreshed: GitHub rate limit reached.)";
		}
	}
}
