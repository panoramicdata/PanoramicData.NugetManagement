using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Runs queued work. One method per <see cref="WorkKind"/>, none of which knows anything about a
/// browser.
/// </summary>
/// <remarks>
/// These bodies used to live in the dashboard component and were executed by the circuit that
/// queued them, which meant closing the tab cancelled the work and every body was free to reach
/// into the component's fields, clear its console and write to its clipboard. With up to twenty
/// lanes running at once none of that survives: there is no one console to clear, no one clipboard
/// to win, and no circuit to render. What each body needs it now takes from the cache and the
/// descriptor, and what it has to say it logs.
/// </remarks>
/// <param name="dashboard">Discovery, assessment, remediation, build, test and publish.</param>
/// <param name="cache">The estate as last discovered; the source of the row each item acts on.</param>
/// <param name="localRepo">Clones and the git operations performed directly on them.</param>
/// <param name="runtimeSettings">User settings, chiefly which repositories are excluded.</param>
/// <param name="fanOut">Turns a discovery's findings into one queued item per repository.</param>
/// <param name="gitHubTokens">
/// The signed-in user's GitHub token, handed forward by a circuit. Work runs with no request behind
/// it, so this — not the accessor — is where the token comes from.
/// </param>
/// <param name="httpContextAccessor">
/// A fallback source for the same token, for the case where an executor is somehow invoked on a
/// request thread. On the runner's own threads it always yields nothing.
/// </param>
/// <param name="logger">
/// Where the work narrates itself. The UI console mirrors this category, so a line logged here
/// reaches the console the item was queued from without the work knowing that console exists.
/// </param>
/// <param name="remediations">
/// Which rules can be fixed automatically. Dependabot triage asks this to tell a pull request an
/// existing remediation will handle from one that needs a remediation written.
/// </param>
/// <param name="triageRunner">
/// Carries out what triage decided. Injected rather than constructed per item so that many
/// repository lanes finding the same gap still produce one issue between them.
/// </param>
/// <param name="ollamaGate">
/// Bounds how many AI fixes are talking to the model at once. Shared, because the limit is a property
/// of the server and not of any one item.
/// </param>
/// <param name="playbooks">The per-rule instructions an AI fix is prompted with.</param>
/// <param name="sessionLogs">Where AI fix sessions are logged in full, or null to log nothing.</param>
/// <param name="governance">Writes what was done to the governance ledger, or null to record nothing.</param>
public sealed partial class WorkExecutors(
	DashboardService dashboard,
	DashboardCacheService cache,
	LocalRepoService localRepo,
	RuntimeSettingsService runtimeSettings,
	WorkFanOut fanOut,
	GitHubTokenProvider gitHubTokens,
	IHttpContextAccessor httpContextAccessor,
	ILogger<WorkExecutors> logger,
	Remediations.RemediationRegistry remediations,
	DependabotTriageRunner triageRunner,
	OllamaGate ollamaGate,
	AiPlaybookRegistry playbooks,
	AiSessionLogStore? sessionLogs = null,
	Governance.GovernanceRecorder? governance = null)
{
	/// <summary>
	/// Opens the on-disk log for one AI fix session and says where it is.
	/// </summary>
	/// <remarks>
	/// The transcript the pane shows is bounded and lost on restart; this is the record that survives, and
	/// is what a prompt or tool change is judged against. A store that is not configured yields a log that
	/// records nothing rather than a null every caller must check.
	/// </remarks>
	private AiSessionLog OpenSessionLog(string repositoryFullName, string workId)
	{
		if (sessionLogs is null)
		{
			return AiSessionLog.None;
		}

		var log = sessionLogs.Open(repositoryFullName, workId);

		if (log.Path.Length > 0)
		{
			Say($"📝 Session log: {log.Path}");
		}

		return log;
	}

	/// <summary>
	/// Writes what a session was given, which is the first record of its log and the one every later
	/// record has to be read against.
	/// </summary>
	private static void LogSessionStart(
		AiSessionLog log,
		AiFixRequest request,
		OllamaOptions ollama,
		AiFixOptions options,
		IReadOnlyList<string>? writablePaths)
		=> log.Write("session_start", new Dictionary<string, object?>
		{
			["repository"] = request.RepositoryFullName,
			["rule"] = request.RuleId,
			["rule_name"] = request.RuleName,
			["model"] = ollama.Model,
			["context_window"] = ollama.ContextWindow,
			["prompt_version"] = AiFixPrompt.Version,
			["tools"] = AiFixSession.ToolSpecs.Select(spec => spec.Name).ToList(),
			["max_turns_per_attempt"] = options.MaxTurnsPerAttempt,
			["max_attempts"] = options.MaxAttempts,
			["writable_paths"] = writablePaths,
			["system_prompt"] = request.SystemPrompt,
			["task"] = request.Task
		});

	/// <summary>Every kind this service knows how to run.</summary>
	/// <remarks>
	/// Exposed so a test can assert it covers <see cref="WorkKind"/> in full. A kind that can be
	/// queued but not run would sit in a lane for ever, blocking everything behind it.
	/// </remarks>
	public static IReadOnlySet<WorkKind> SupportedKinds { get; } = new HashSet<WorkKind>
	{
		WorkKind.Clone, WorkKind.Reassess, WorkKind.FixAll, WorkKind.FixCategory, WorkKind.FixRule,
		WorkKind.FixWithAiRule,
		WorkKind.AnalyseIssue, WorkKind.FixWithAiIssue, WorkKind.UnstickWithAi,
		WorkKind.TriageDependabot,
		WorkKind.Build, WorkKind.Test, WorkKind.GitSync, WorkKind.CommitAndPush, WorkKind.Publish,
		WorkKind.RediscoverOrganization, WorkKind.DiscoverReassessTargets,
		WorkKind.DiscoverCloneTargets, WorkKind.RefreshAll
	};

	/// <summary>Runs one queued item.</summary>
	/// <param name="item">The item to run; its <see cref="WorkItem.Descriptor"/> selects the body.</param>
	/// <param name="progress">Reports progress lines into the item's tree node.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	public async Task ExecuteAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		// Held for the duration of the run so Say can reach it. Safe as an instance field because
		// WorkRunnerService resolves a fresh scope — and therefore a fresh WorkExecutors — for every
		// item, so two lanes running together never share one.
		_running = item;

		try
		{
			await DispatchAsync(item, progress, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			// In a finally, and so also after a failure or a stop: work that was interrupted half way
			// through rewriting files has changed the tree just as surely as work that finished, and
			// the remembered build result describes neither.
			ForgetBuildStatusIfInvalidated(item);
		}
	}

	/// <summary>
	/// Throws away the repository's remembered build and test results when the work that just ran could have
	/// changed what is on disk.
	/// </summary>
	/// <param name="item">The item that ran.</param>
	private void ForgetBuildStatusIfInvalidated(WorkItem item)
	{
		if (!BuildStatusLifetime.Invalidates(item.Descriptor.Kind))
		{
			return;
		}

		var row = RowFor(item);
		if (row is null || !row.HasVerificationResults)
		{
			return;
		}

		row.ForgetVerificationResults();
		cache.UpsertRow(row);
	}

	private Task DispatchAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
		=> item.Descriptor.Kind switch
		{
			WorkKind.Clone => CloneAsync(item, progress, cancellationToken),
			WorkKind.Reassess => ReassessAsync(item, progress, cancellationToken),
			WorkKind.FixAll => FixAllAsync(item, progress, cancellationToken),
			WorkKind.FixCategory => FixCategoryAsync(item, progress, cancellationToken),
			WorkKind.FixRule => FixRuleAsync(item, progress, cancellationToken),
			WorkKind.FixWithAiRule => FixWithAiRuleAsync(item, progress, cancellationToken),
			WorkKind.AnalyseIssue => AnalyseIssueAsync(item, progress, cancellationToken),
			WorkKind.FixWithAiIssue => FixWithAiIssueAsync(item, progress, cancellationToken),
			WorkKind.UnstickWithAi => UnstickWithAiAsync(item, progress, cancellationToken),
			WorkKind.TriageDependabot => TriageDependabotAsync(item, progress, cancellationToken),
			WorkKind.Build => BuildAsync(item, progress, cancellationToken),
			WorkKind.Test => TestAsync(item, progress, cancellationToken),
			WorkKind.GitSync => GitSyncAsync(item, progress, cancellationToken),
			WorkKind.CommitAndPush => CommitAndPushAsync(item, progress, cancellationToken),
			WorkKind.Publish => PublishAsync(item, progress, cancellationToken),
			WorkKind.RediscoverOrganization => RediscoverOrganizationAsync(item, progress, cancellationToken),
			WorkKind.DiscoverReassessTargets => DiscoverReassessTargetsAsync(item, progress, cancellationToken),
			WorkKind.DiscoverCloneTargets => DiscoverCloneTargetsAsync(item, progress, cancellationToken),
			WorkKind.RefreshAll => RefreshAllAsync(item, progress, cancellationToken),
			_ => throw new NotSupportedException($"No executor for {item.Descriptor.Kind}.")
		};

	/// <summary>The item currently running, so <see cref="Say"/> can reach its transcript.</summary>
	private WorkItem? _running;

	/// <summary>
	/// Says something in the console the item was queued from, and in the item's own transcript.
	/// </summary>
	/// <remarks>
	/// Both, not either. The UI console subscribes to this logging category and answers "what is this
	/// application doing"; the transcript answers "what did this item do", which the console stops
	/// being able to answer the moment two lanes interleave into it. The line is passed as an argument
	/// rather than as the template so braces in a path or a git message are not read as
	/// structured-logging placeholders.
	/// </remarks>
	/// <param name="line">The line to say.</param>
	private void Say(string line)
	{
		_running?.Transcript.Append(WorkLineKind.Output, line);
		logger.LogInformation("{ConsoleLine}", line);
	}

	/// <summary>
	/// Notes that a restored item names a repository the estate no longer has, so its body is skipped.
	/// </summary>
	/// <param name="item">The item that cannot run.</param>
	private void SayRepositoryGone(WorkItem item)
		=> logger.LogWarning(
			"Skipping {Title}: {Repository} is no longer in the estate.",
			item.Title,
			item.RepositoryFullName);

	/// <summary>
	/// Builds one repository, and on failure leaves behind the AI prompt for the failure.
	/// </summary>
	/// <remarks>
	/// The prompt used to be copied to the clipboard and an IDE opened alongside it. Twenty lanes
	/// finishing together would race twenty of each, so it is stored on the item and claimed from the
	/// UI instead. The console lines it quotes are the ones this build itself produced, rather than
	/// whatever happened to be in a shared console buffer.
	/// </remarks>
	/// <param name="item">The item naming the repository to build.</param>
	/// <param name="progress">Unused: a build reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task BuildAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var output = new List<string>();
		Say("▶ Building...");

		try
		{
			await dashboard.BuildAsync(
				row,
				line =>
				{
					output.Add(line);
					Say(line);
				},
				cancellationToken).ConfigureAwait(false);

			var succeeded = row.Status == PackageStatus.BuildSucceeded;

			if (succeeded)
			{
				Say("✅ Build succeeded");
				item.Succeeded = true;
			}
			else
			{
				Say("❌ Build failed");
				item.GeneratedPrompt = DashboardService.GenerateConciseWorkflowFailurePrompt(row, "build", output);
				item.Succeeded = false;
			}

			RememberBuildResult(row, succeeded ? RepositoryBuildState.Succeeded : RepositoryBuildState.Failed);
		}
		catch (OperationCanceledException)
		{
			// Stopping is not failing. Rethrown so the runner marks the item Cancelled and logs
			// the stop, and so the step badge is left alone rather than painted red for work the
			// user chose to end.
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
			item.Succeeded = false;

			// A build that could not be run is a repository that does not build here, which is what
			// the badge is asked. Leaving it as not-known would hide it among the never-built.
			RememberBuildResult(row, RepositoryBuildState.Failed);
		}
	}

	/// <summary>
	/// Records what a build did, so the estate can be read at a glance rather than one repository at
	/// a time.
	/// </summary>
	/// <param name="row">The repository that was built.</param>
	/// <param name="state">What the build did.</param>
	private void RememberBuildResult(RepositoryDashboardRow row, RepositoryBuildState state)
	{
		row.LastBuildState = state;
		row.LastBuiltAtUtc = DateTimeOffset.UtcNow;
		cache.UpsertRow(row);
	}

	/// <summary>
	/// Records what a test run did, so the estate can be read at a glance rather than one repository at
	/// a time.
	/// </summary>
	/// <param name="row">The repository that was tested.</param>
	/// <param name="state">What the run did.</param>
	private void RememberTestResult(RepositoryDashboardRow row, RepositoryTestState state)
	{
		row.RememberTestResult(state, DateTimeOffset.UtcNow);
		cache.UpsertRow(row);
	}

	/// <summary>
	/// Clones one repository into the local checkout root and marks any cached rows it backs as
	/// locally available.
	/// </summary>
	/// <remarks>
	/// The clone URL is not read from the descriptor — every GitHub repository's is derivable from its
	/// full name — which is what lets <see cref="WorkFanOut.EnqueueClone"/> queue one of these per
	/// repository from nothing more than the name.
	/// </remarks>
	/// <param name="item">The item naming the repository to clone.</param>
	/// <param name="progress">Unused: a clone reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task CloneAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var repositoryFullName = item.RepositoryFullName;
		if (repositoryFullName is null)
		{
			logger.LogWarning("Skipping {Title}: no repository was given to clone.", item.Title);
			return;
		}

		var cloneUrl = $"https://github.com/{repositoryFullName}.git";
		Say($"⬇️ Cloning {repositoryFullName}...");

		try
		{
			var (success, output) = await localRepo.CloneAsync(cloneUrl, repositoryFullName, Say, cancellationToken).ConfigureAwait(false);

			if (success)
			{
				Say($"✅ Cloned {repositoryFullName}");
				await AdoptClonedRepositoryAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				Say($"❌ {repositoryFullName}: {output}");
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ {repositoryFullName}: {ex.Message}");
		}
	}

	/// <summary>
	/// Updates every cached row backed by a freshly cloned repository so the tree shows it as local
	/// immediately. A repository can host several packages, hence every matching row.
	/// </summary>
	/// <param name="repositoryFullName">The repository that was just cloned.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task AdoptClonedRepositoryAsync(string repositoryFullName, CancellationToken cancellationToken)
	{
		var rows = (cache.GetCachedRows() ?? [])
			.Where(r => string.Equals(r.RepositoryFullName, repositoryFullName, StringComparison.OrdinalIgnoreCase))
			.ToList();

		if (rows.Count == 0)
		{
			return;
		}

		var localPath = localRepo.GetLocalPath(repositoryFullName);
		var branch = await localRepo.GetCurrentBranchAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);
		var isClean = await localRepo.IsWorkingTreeCleanAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);

		foreach (var row in rows)
		{
			row.IsClonedLocally = true;
			row.LocalPath = localPath;
			row.CurrentBranch = branch;
			row.IsWorkingTreeClean = isClean;
			cache.UpsertRow(row);
		}
	}

	/// <summary>
	/// Turns the clone dialog's selection into one queued clone per repository.
	/// </summary>
	/// <remarks>
	/// The "discovery" here is nominal: the dialog already asked GitHub which repositories exist and
	/// the user already chose which of them to take, so this step exists only so the fan-out — and the
	/// git calls it leads to — happens off the circuit that opened the dialog, exactly like every other
	/// discovery kind. The selection travels as a comma-separated <c>fullNames</c> parameter on the
	/// descriptor, since <see cref="WorkDescriptor"/> carries only strings and cannot hold the dialog's
	/// richer candidate objects.
	/// </remarks>
	/// <param name="item">The item naming the organisation and, via <c>fullNames</c>, which repositories to clone.</param>
	/// <param name="progress">Unused: this step reports no sub-steps of its own.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private Task DiscoverCloneTargetsAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var organization = item.Organization;
		var fullNames = item.Descriptor.Parameter("fullNames")?
			.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];

		if (organization is null || fullNames.Length == 0)
		{
			logger.LogWarning("Skipping {Title}: no organisation or repositories to clone were given.", item.Title);
			return Task.CompletedTask;
		}

		var targets = fullNames
			.Select(fullName => new RepositoryCloneCandidate
			{
				Name = fullName.Split('/')[^1],
				FullName = fullName,
				CloneUrl = $"https://github.com/{fullName}.git"
			})
			.ToList();

		var queued = fanOut.EnqueueClone(organization, targets, item.ConsoleNodeKey);
		Say($"▶ Queued {queued} of {targets.Count} repositories to clone.");
		return Task.CompletedTask;
	}

	/// <summary>
	/// Re-reads one organisation's package list from NuGet, keeps whatever assessments already existed
	/// for repositories that are unchanged, and fans out re-assessment across the rest.
	/// </summary>
	/// <param name="item">The item naming the organisation to rediscover.</param>
	/// <param name="progress">Unused: this step reports no sub-steps of its own.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task RediscoverOrganizationAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var organization = item.Organization;
		if (organization is null)
		{
			logger.LogWarning("Skipping {Title}: no organisation was given.", item.Title);
			return;
		}

		Say($"▶ Discovering {organization} packages...");

		try
		{
			var existingForOrg = (cache.GetCachedRows() ?? [])
				.Where(r => string.Equals(r.Organization, organization, StringComparison.OrdinalIgnoreCase))
				.ToList();

			var freshRows = await dashboard.DiscoverPackagesAsync(
				organization,
				cancellationToken,
				await CreateGitHubClientAsync().ConfigureAwait(false)).ConfigureAwait(false);
			SayUnreadNuspecs();

			// Carry over assessments already held, so rediscovery does not throw away results for
			// packages that have not changed.
			var existingByRepository = existingForOrg
				.Where(r => r.Assessment is not null)
				.ToDictionary(r => r.RepositoryFullName, StringComparer.OrdinalIgnoreCase);

			foreach (var row in freshRows)
			{
				if (existingByRepository.TryGetValue(row.RepositoryFullName, out var existing))
				{
					row.Assessment = existing.Assessment;
					row.CategorySummaries = existing.CategorySummaries;
				}
			}

			// Swap in this organisation's rows only; every other organisation's are left exactly as
			// they were.
			List<RepositoryDashboardRow> allRows =
			[
				.. (cache.GetCachedRows() ?? [])
					.Where(r => !string.Equals(r.Organization, organization, StringComparison.OrdinalIgnoreCase)),
				.. freshRows
			];
			cache.SetRows(allRows);

			var assessable = freshRows.Where(r => r.IsGoverned).ToList();
			var queued = fanOut.EnqueueReassess(organization, assessable, item.ConsoleNodeKey);
			Say($"✅ Rediscovered {organization}: queued {queued} of {assessable.Count} repositories to re-assess.");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to rediscover {Organization}", organization);
			Say($"❌ Rediscover failed: {ex.Message}");
		}
	}

	/// <summary>
	/// Works out which repositories a re-assessment run covers — every governed, non-excluded
	/// repository, optionally narrowed to one organisation — and fans re-assessment out across them.
	/// </summary>
	/// <remarks>
	/// Grouped by each repository's own organisation rather than enqueued under the item's: a
	/// "re-assess everything" run spans organisations the item itself does not belong to, and
	/// <see cref="WorkFanOut.EnqueueReassess"/> needs the organisation each repository actually is in.
	/// </remarks>
	/// <param name="item">The item naming the organisation to scope to, or none for every organisation.</param>
	/// <param name="progress">Unused: this step reports no sub-steps of its own.</param>
	/// <param name="cancellationToken">Unused: discovery here is a synchronous cache read.</param>
	private Task DiscoverReassessTargetsAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var organization = item.Organization;
		var rows = cache.GetCachedRows() ?? [];

		var assessable = rows
			.Where(r => r.IsGoverned)
			.Where(r => !runtimeSettings.IsRepositoryExcluded(r.RepositoryFullName))
			.Where(r => organization is null
				|| string.Equals(r.Organization, organization, StringComparison.OrdinalIgnoreCase))
			.ToList();

		if (assessable.Count == 0)
		{
			Say("Nothing to re-assess.");
			return Task.CompletedTask;
		}

		var queued = 0;
		foreach (var group in assessable.GroupBy(r => r.Organization))
		{
			queued += fanOut.EnqueueReassess(group.Key, [.. group], item.ConsoleNodeKey);
		}

		Say($"▶ Queued {queued} of {assessable.Count} repositories to re-assess.");
		return Task.CompletedTask;
	}

	/// <summary>
	/// Rediscovers every organisation's package list from NuGet, keeps whatever assessments already
	/// existed for repositories that are unchanged, and fans out re-assessment across the rest.
	/// </summary>
	/// <param name="item">The item this run was queued as; carries no organisation, since it spans all of them.</param>
	/// <param name="progress">Unused: this step reports no sub-steps of its own.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task RefreshAllAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		Say("▶ Discovering packages...");

		try
		{
			var freshRows = await dashboard.DiscoverPackagesAsync(
				cancellationToken: cancellationToken,
				github: await CreateGitHubClientAsync().ConfigureAwait(false)).ConfigureAwait(false);
			SayUnreadNuspecs();

			var existingByRepository = (cache.GetCachedRows() ?? [])
				.Where(r => r.Assessment is not null)
				.ToDictionary(r => r.RepositoryFullName, StringComparer.OrdinalIgnoreCase);

			foreach (var row in freshRows)
			{
				if (existingByRepository.TryGetValue(row.RepositoryFullName, out var existing))
				{
					row.Assessment = existing.Assessment;
					row.CategorySummaries = existing.CategorySummaries;
				}
			}

			cache.Update(freshRows);

			var assessable = freshRows
				.Where(r => r.IsGoverned)
				.Where(r => !runtimeSettings.IsRepositoryExcluded(r.RepositoryFullName))
				.ToList();

			var queued = 0;
			var triaged = 0;
			foreach (var group in assessable.GroupBy(r => r.Organization))
			{
				var rows = group.ToList();
				queued += fanOut.EnqueueReassess(group.Key, rows, item.ConsoleNodeKey);

				// Queued after the re-assessment, on the same lanes, so each triage sees the assessment
				// it needs. Only the whole-estate refresh does this: a plain re-assess stays read-only,
				// because "assess everything" should not silently close anybody's pull requests.
				triaged += fanOut.EnqueueTriageDependabot(group.Key, rows, item.ConsoleNodeKey);
			}

			Say($"✅ Refreshed {freshRows.Count} packages, queued {queued} of {assessable.Count} "
				+ $"repositories to re-assess and {triaged} to triage for Dependabot.");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to refresh dashboard");
			Say($"❌ Refresh failed: {ex.Message}");
		}
	}

	/// <summary>
	/// Notes in the console when a package's nuspec could not be read during the discovery that just
	/// ran, so a transient GitHub or network failure is visible rather than silently leaving a package
	/// looking ungoverned.
	/// </summary>
	private void SayUnreadNuspecs()
	{
		var unread = cache.GetUngovernedPackages()
			.Where(package => package.Reason.StartsWith(UngovernedPackage.LookupFailedReasonPrefix, StringComparison.Ordinal))
			.Select(package => package.PackageId)
			.ToList();

		if (unread.Count == 0)
		{
			return;
		}

		Say($"⚠️ Could not read the nuspec for {string.Join(", ", unread)}. "
			+ "Their repositories are unchanged from the last successful discovery; rediscover to try again.");
	}

	/// <summary>
	/// Whether a repository has retired every package it published, and so leaves the estate.
	/// </summary>
	/// <remarks>
	/// A repository that never published anything has retired nothing: GitHub-derived rows have no
	/// packages by design, and treating "none listed" as "all retired" deleted every live unpackaged
	/// repository on its first re-assessment.
	/// </remarks>
	/// <param name="publishedCount">How many packages the row publishes.</param>
	/// <param name="listedCount">How many of those are still listed on NuGet.</param>
	internal static bool HasRetiredEveryPackage(int publishedCount, int listedCount)
		=> publishedCount > 0 && listedCount == 0;

	/// <summary>
	/// Re-checks one repository's packages against the NuGet listing and, if any are still listed or
	/// it publishes none, re-assesses it against every rule.
	/// </summary>
	/// <remarks>
	/// A repository leaves the estate only when every package it publishes has been retired. Dropping
	/// it because one of several was unlisted would take the still-listed ones with it.
	/// </remarks>
	/// <param name="item">The item naming the repository to re-assess.</param>
	/// <param name="progress">Unused: a re-assessment reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task ReassessAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		Say($"▶ Checking NuGet listing for {row.RepositoryFullName}...");

		try
		{
			cancellationToken.ThrowIfCancellationRequested();

			var listed = new List<string>();
			foreach (var package in row.Packages)
			{
				if (await dashboard.IsPackageListedAsync(package.PackageId).ConfigureAwait(false))
				{
					listed.Add(package.PackageId);
				}
			}

			if (HasRetiredEveryPackage(row.Packages.Count, listed.Count))
			{
				Say($"⚠️ {row.RepositoryFullName} publishes nothing still listed on NuGet — removing from cache.");
				cache.RemoveRow(row.RepositoryFullName);
				return;
			}

			if (listed.Count < row.Packages.Count)
			{
				var retired = row.Packages
					.Select(package => package.PackageId)
					.Except(listed, StringComparer.OrdinalIgnoreCase);

				Say($"ℹ️ {row.RepositoryFullName}: {string.Join(", ", retired)} no longer listed on NuGet.");
			}

			cancellationToken.ThrowIfCancellationRequested();
			Say($"▶ Assessing {row.RepositoryFullName}...");

			// Hoisted out of the else: the local path wants a client too, to read the repository's
			// inbox. Constructing one is local and free, so both branches can simply have one.
			var github = await CreateGitHubClientAsync().ConfigureAwait(false);

			if (row.IsClonedLocally && row.LocalPath is not null)
			{
				await dashboard.AssessLocalRepositoryAsync(row, cancellationToken, github).ConfigureAwait(false);
				await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
				await SayDirtyWorkingTreePreviewAsync(row).ConfigureAwait(false);
			}
			else
			{
				await dashboard.AssessRepositoryAsync(row, github).ConfigureAwait(false);
			}

			cache.UpsertRow(row);
			Say($"✅ Assessment complete for {row.RepositoryFullName}");
		}
		catch (OperationCanceledException)
		{
			// The pump says so, and says it the same way for every step.
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Assessment failed: {ex.Message}");
		}
	}

	/// <summary>
	/// Applies every available auto-remediation to one repository, re-assesses it, and leaves behind
	/// an AI prompt for whatever it could not fix.
	/// </summary>
	/// <remarks>
	/// The prompt used to be copied to the clipboard and an IDE opened alongside it. Twenty lanes
	/// finishing together would race twenty of each, so it is stored on the item and claimed from the
	/// UI instead.
	/// </remarks>
	/// <param name="item">The item naming the repository to fix.</param>
	/// <param name="progress">Unused: a fix-all reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task FixAllAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		Say("▶ Applying all auto-remediations...");

		// Whether the write phase finished. Everything after it only reads the clone, so a Stop from
		// that point on has nothing to undo — see the catch below.
		var remediationCompleted = false;

		try
		{
			var applied = await dashboard.ApplyRemediationsAsync(row, Say, cancellationToken).ConfigureAwait(false);
			remediationCompleted = true;
			governance?.RecordRemediation(item, row.RepositoryFullName, null, applied);

			await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
			cache.UpsertRow(row);
			Say($"✅ Applied {applied.Count} remediation(s)");

			Say("▶ Re-assessing...");

			// The client is what lets the local assessment read the repository's inbox, so a
			// re-assessment after a fix reports the same issue state a fresh one would.
			await dashboard.AssessLocalRepositoryAsync(
				row,
				cancellationToken,
				await CreateGitHubClientAsync().ConfigureAwait(false)).ConfigureAwait(false);
			cache.UpsertRow(row);

			// Report remaining issues that have no auto-fix.
			var remaining = row.Assessment?.RuleResults
				.Where(r => !r.Passed && !dashboard.IsAutoRemediable(r))
				.ToList() ?? [];
			if (remaining.Count > 0)
			{
				foreach (var r in remaining)
				{
					Say($"⚠️ [{r.RuleId}] {r.RuleName} — no auto-fix available");
				}

				Say($"ℹ️ {remaining.Count} issue(s) require manual fix. Generating AI prompt...");
				item.GeneratedPrompt = DashboardService.GenerateRemediationPromptForFailures(row, remaining, includeInfo: false);
			}
			else if (row.TotalFailures == 0)
			{
				Say("🎉 No remaining issues — all rules pass.");
			}
		}
		catch (OperationCanceledException)
		{
			// Atomic over the WRITE phase only. A remediation stopped part-way is undone, so the clone
			// is left as it was found rather than carrying half a fix into the next commit — but once
			// the remediation has landed, the re-assessment that follows only reads the clone, and
			// throwing away a fix the user asked for because a subsequent read was interrupted would
			// destroy work they never asked to undo.
			await RevertIfRemediationIncompleteAsync(row, remediationCompleted).ConfigureAwait(false);
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Applies the auto-remediations of one assessment category to a repository, then re-assesses it.
	/// </summary>
	/// <param name="item">The item naming the repository and, via <c>category</c>, which category to fix.</param>
	/// <param name="progress">Unused: a category fix reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task FixCategoryAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var category = Enum.Parse<AssessmentCategory>(item.Descriptor.Parameter("category")!);
		Say($"▶ Fixing {category}...");

		// Whether the write phase finished. Everything after it only reads the clone, so a Stop from
		// that point on has nothing to undo — see the catch below.
		var remediationCompleted = false;

		try
		{
			var applied = await dashboard.ApplyCategoryRemediationsAsync(row, category, Say, cancellationToken).ConfigureAwait(false);
			remediationCompleted = true;
			governance?.RecordRemediation(item, row.RepositoryFullName, null, applied);

			await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
			cache.UpsertRow(row);
			Say($"✅ Applied {applied.Count} remediation(s) for {category}");

			Say("▶ Re-assessing...");

			// The client is what lets the local assessment read the repository's inbox, so a
			// re-assessment after a fix reports the same issue state a fresh one would.
			await dashboard.AssessLocalRepositoryAsync(
				row,
				cancellationToken,
				await CreateGitHubClientAsync().ConfigureAwait(false)).ConfigureAwait(false);
			cache.UpsertRow(row);
		}
		catch (OperationCanceledException)
		{
			// Atomic over the WRITE phase only. A remediation stopped part-way is undone, so the clone
			// is left as it was found rather than carrying half a fix into the next commit — but once
			// the remediation has landed, the re-assessment that follows only reads the clone, and
			// throwing away a fix the user asked for because a subsequent read was interrupted would
			// destroy work they never asked to undo.
			await RevertIfRemediationIncompleteAsync(row, remediationCompleted).ConfigureAwait(false);
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Has the local model fix one rule that no deterministic remediation covers.
	/// </summary>
	/// <remarks>
	/// Runs on the repository's own lane rather than a lane of its own, so that nothing else can be
	/// writing to the same working tree while the model is. What bounds the load on the model's server is
	/// <see cref="OllamaGate"/>, which is a separate concern from keeping one clone to one writer.
	/// <para>
	/// On failure the clone is reverted. Half-finished edits from a model that misunderstood the task are
	/// worse than no attempt: somebody has to unpick them, and the transcript in this item's output says
	/// what was tried either way.
	/// </para>
	/// </remarks>
	/// <param name="item">The item naming the repository and the rule.</param>
	/// <param name="progress">Unused: the transcript goes to the console.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task FixWithAiRuleAsync(
		WorkItem item,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		var row = RowFor(item);

		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var ruleId = item.Descriptor.Parameter("ruleId")!;
		var targetPath = item.Descriptor.Parameter("path");
		var ollama = runtimeSettings.Ollama;

		if (!ollama.IsConfigured)
		{
			Say("⚠️ No Ollama server is configured — set one under Settings, Ollama Config.");
			return;
		}

		if (row.LocalPath is null || !row.IsClonedLocally)
		{
			Say($"⏭️ {row.RepositoryFullName} is not cloned locally, and the model edits files on disk.");
			return;
		}

		var result = row.Assessment?.RuleResults
			.FirstOrDefault(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

		if (result is null || result.Passed)
		{
			Say($"⏭️ {ruleId} is no longer failing on {row.RepositoryFullName}.");
			return;
		}

		// Fix and Fix with AI are disjoint. A rule that gained a remediation since this item was queued
		// belongs to Fix now, and doing it here would spend a GPU on work a function can do exactly.
		if (remediations.Get(ruleId) is not null)
		{
			Say($"⏭️ {ruleId} now has an automatic remediation — use Fix instead.");
			return;
		}

		var succeeded = false;
		Func<int>? undoWrites = null;
		Func<Task<string?>>? progressWorthKeeping = null;

		// What the governance ledger needs once the session is over, gathered as it goes because the
		// session's own variables are out of scope by the time it is recorded.
		AiFixRequest? recordedRequest = null;
		AiFixOutcome? recordedOutcome = null;
		AiFixToolbox? recordedToolbox = null;
		string? recordedLogPath = null;

		try
		{
			// Queued behind whatever else wants the model. Entered before any file is touched, so a wait
			// for the server never leaves a half-edited clone lying around.
			Say($"⏳ Waiting for the model ({ollama.Model})...");
			using var hold = await ollamaGate.EnterAsync(cancellationToken).ConfigureAwait(false);

			using var client = new Ollama.Api.OllamaClient(new Ollama.Api.OllamaClientOptions
			{
				Uri = new Uri(ollama.BaseUrl!),
				ApiKey = ollama.ApiKey,
				Timeout = TimeSpan.FromMilliseconds(ollama.RequestTimeoutMs)
			});

			// Before anything below can touch the clone: what is already changed is not this session's doing.
			await RecordPreExistingChangesAsync(item, row.LocalPath, cancellationToken).ConfigureAwait(false);

			// TST-10 is the one remotely graded rule that can be measured locally: the same tests, run
			// under coverage, give a figure close to the one Codacy will later report. Measured before the
			// model starts, so it is told where it stands and a repository that cannot be measured is
			// declined now rather than after twelve turns of writing tests nothing can score.
			var coverageMode = string.Equals(ruleId, "TST-10", StringComparison.OrdinalIgnoreCase);
			CoverageRun? baseline = null;

			// The latest coverage figure, shown in the pane's status line next to the turn and the clock.
			string? coverageNow = null;
			IReadOnlyList<string> testFolders = [];
			IReadOnlyList<string> testProjects = [];

			if (coverageMode)
			{
				testProjects = CoverageMeasurement.FindTestProjects(row.LocalPath);

				testFolders =
				[
					.. testProjects
						.Select(project => (Path.GetDirectoryName(project) ?? string.Empty).Replace('\\', '/'))
						.Where(folder => folder.Length > 0)
						.Distinct(StringComparer.OrdinalIgnoreCase)
				];

				if (testFolders.Count == 0)
				{
					Say($"⏭️ {row.RepositoryFullName} has no test project to add tests to, so coverage cannot be raised here.");
					return;
				}

				Say("📏 Measuring coverage before the model starts...");
				baseline = await CoverageMeasurement.MeasureAsync(row.LocalPath, null, cancellationToken).ConfigureAwait(false);

				if (baseline.Report is null)
				{
					Say($"⏭️ Coverage cannot be measured locally: {baseline.Problem}");
					return;
				}

				coverageNow = CoverageStatus(baseline);

				Say($"📏 {baseline.Report.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}% to start, aiming for "
					+ $"{_coverageTarget.ToString("0", CultureInfo.InvariantCulture)}%.");
			}

			// What already fails here, before the model has touched anything. CI gives these tests the
			// credentials this sandbox withholds, so they fail in both worlds for a reason that has nothing
			// to do with coverage — and are told to the model as known, not left for it to discover.
			IReadOnlySet<string> knownFailures = baseline?.FailedTests ?? new HashSet<string>();

			// A file graded down for duplication is repeated across files, so editing it alone cannot fix it, and
			// the only local check — does it build — passes for a change that joins two lines. Measured here
			// instead, so the session is told where the repeats are, may touch the files that share them, and is
			// judged on whether the file really became less repetitive. Aruba.Api's interfaces were "fixed" three
			// and four times each by reformatting, and graded F every time.
			DuplicationReport? duplicationBefore = null;

			if (string.Equals(ruleId, "CQ-06", StringComparison.OrdinalIgnoreCase) && targetPath is { Length: > 0 })
			{
				var measured = await Task.Run(() => DuplicationMeasurement.Measure(row.LocalPath, targetPath), cancellationToken)
					.ConfigureAwait(false);

				// Below this the grade is about something else — complexity, an issue — and a duplication check
				// would reject a change that was right.
				if (measured is { Percent: >= 15 })
				{
					duplicationBefore = measured;
					Say($"📏 {targetPath} is {measured.Percent.ToString("0", CultureInfo.InvariantCulture)}% repeated by a local "
						+ $"measure, across {measured.PeerFiles.Count.ToString(CultureInfo.InvariantCulture)} other file(s).");
				}
			}

			var duplicationMode = duplicationBefore is not null;
			var targetFolder = duplicationMode ? Path.GetDirectoryName(targetPath)?.Replace('\\', '/') : null;

			var toolbox = new AiFixToolbox(
				row.LocalPath,
				build: async token =>
				{
					var captured = new List<string>();
					await dashboard.BuildAsync(row, captured.Add, token, restore: true).ConfigureAwait(false);
					return SummariseRun("Build", row.Status == PackageStatus.BuildSucceeded, captured);
				},
				test: async token =>
				{
					var captured = new List<string>();
					await dashboard.RunTestsAsync(row, captured.Add, token, sandbox: true).ConfigureAwait(false);
					return SummariseRun("Tests", row.Status == PackageStatus.TestsPassed, captured);
				},
				// Confined to the test project, so "raising coverage" cannot be done by deleting the code
				// that is uncovered or by editing it until it is easy to cover.
				writableFolders: coverageMode
					? testFolders
					: targetFolder is { Length: > 0 } ? [targetFolder] : null,

				// The file and the files that share its repeats, and nothing else: moving a repeat into one place
				// means changing every copy.
				writablePaths: duplicationMode ? [targetPath!, .. duplicationBefore!.PeerFiles] : null,
				coverage: coverageMode
					? async token =>
					{
						var run = await CoverageMeasurement.MeasureAsync(row.LocalPath, null, token).ConfigureAwait(false);
						coverageNow = CoverageStatus(run);

						return DescribeCoverage(run, knownFailures);
					}
					: null,

				// Only new files: "add tests" is not a licence to change the ones that exist.
				newFilesOnly: coverageMode || duplicationMode,

				// Whether the test project still compiles, said in the result of the write that broke it.
				afterWrite: coverageMode
					? (path, token) => BuildFeedbackAsync(row.LocalPath, testProjects, path, token)
					: null,

				// Not offered where the check already runs the tests for the model: in a repository whose tests call
				// a live service each run is minutes, and one session spent twenty-four of them on six calls that
				// could not change anything it was able to do.
				offerRunTests: !(IsRemotelyGraded(ruleId) && !coverageMode));

			undoWrites = toolbox.Undo;
			recordedToolbox = toolbox;

			// A coverage session that falls short has usually still done most of the work, and throwing it
			// all away makes the next attempt start from nothing. If what is left compiles, passes, and has
			// moved the figure, it is progress, and keeping it is what lets the estate climb in steps.
			if (coverageMode && baseline?.Report is { } startingPoint)
			{
				progressWorthKeeping = async () =>
				{
					if (toolbox.FilesWritten.Count == 0)
					{
						return null;
					}

					var final = await CoverageMeasurement.MeasureAsync(row.LocalPath, null, CancellationToken.None).ConfigureAwait(false);

					return final.Report is { } reached
						&& (final.TestsPassed || OnlyKnownFailures(final, knownFailures))
						&& reached.LinePercent >= startingPoint.LinePercent + _coverageProgressWorthKeeping
							? $"{startingPoint.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}% to {reached.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}%"
							: null;
				};
			}

			// Noted before the model touches anything. A repository that already fails to build cannot be
			// held to a build it never had, and would trap the session in a correction it cannot satisfy.
			var buildWasBroken = row.LastBuildState == RepositoryBuildState.Failed;

			var playbook = playbooks.For(ruleId);

			// A rule graded on the published branch cannot answer whether this clone improved, so the
			// loop is told to stop asking it. Retrying is pointless for the same reason: the correction
			// fed back would be the same sentence every time.
			var remotelyGraded = IsRemotelyGraded(ruleId);

			// Which tests already fail here, taken before the model changes anything, so a later failure can be
			// told apart from one it caused. Only worth the time where the check will use it: a remote rule
			// that is not the coverage one, in a repository that builds.
			var verifyTests = remotelyGraded && !coverageMode && !buildWasBroken;
			IReadOnlySet<string> testBaseline = new HashSet<string>();

			if (verifyTests)
			{
				Say("🧪 Noting which tests already fail here, before the model starts...");

				var buildOutput = new List<string>();
				await dashboard.BuildAsync(row, buildOutput.Add, cancellationToken, restore: true).ConfigureAwait(false);

				if (row.Status == PackageStatus.BuildSucceeded)
				{
					var testOutput = new List<string>();
					await dashboard.RunTestsAsync(row, testOutput.Add, cancellationToken, sandbox: true).ConfigureAwait(false);
					testBaseline = CoverageMeasurement.ParseFailedTests(string.Join('\n', testOutput));
				}
				else
				{
					verifyTests = false;
				}
			}

			Say($"▶ {ruleId} on {row.RepositoryFullName} via {ollama.Model}"
				+ $"{(targetPath is { Length: > 0 } ? $", in {targetPath}" : string.Empty)}"
				+ $"{(playbook is null ? " (no playbook — using the advisory)" : string.Empty)}.");

			if (coverageMode)
			{
				Say($"ℹ️ {ruleId} is graded by Codacy on the published branch, so this run is judged on local line "
					+ "coverage from the same tests. Codacy re-grades after you push.");
			}
			else if (remotelyGraded)
			{
				Say($"ℹ️ {ruleId} is graded on the published branch, so this run is judged on whether the "
					+ "change builds. Codacy re-grades after you push.");
			}

			var fixOptions = new AiFixOptions
			{
				// Writing tests is many small steps — read a file, write a test, measure — where a one-line fix
				// is one or two, and a budget sized for the second starves the first.
				MaxTurnsPerAttempt = coverageMode ? Math.Max(ollama.MaxTurnsPerAttempt, _coverageTurnsPerAttempt) : ollama.MaxTurnsPerAttempt,

				// A coverage figure is fresh feedback every attempt, unlike a remote verdict that repeats itself.
				// A remote verdict repeats itself, so a second attempt would be told nothing new — unless the
				// tests are being checked, which gives it something specific to fix.
				MaxAttempts = remotelyGraded && !coverageMode && !verifyTests ? 1 : ollama.MaxAttemptsPerRule,

				// A clock as well as a turn count: a turn that writes a whole test file takes most of a minute.
				TimeBudget = coverageMode ? _coverageTimeBudget : _sessionTimeBudget
			};

			using var sessionLog = OpenSessionLog(row.RepositoryFullName, ruleId);
			recordedLogPath = sessionLog.Path;

			var session = new AiFixSession(
				new OllamaChatModel(client, ollama.Model!, ollama.ContextWindow, ollama.ToDirect()),
				toolbox,
				fixOptions,
				Say,
				// Straight into this item's transcript rather than through Say: a token is a fragment,
				// not a line, and the shared console has no way to render a hundred of them a second.
				AiTranscriptSink.For(item.Transcript),
				sessionLog,
				onStatus: progress.Report,
				statusDetail: () => coverageNow);

			var request = new AiFixRequest(
				row.RepositoryFullName,
				ruleId,
				result.RuleName,
				AiFixPrompt.BuildTask(result, row.RepositoryFullName, playbook, targetPath)
					+ (baseline?.Report is not null
						? $"\nTest project folder (the only place you may write): {string.Join(", ", testFolders)}\n"
							+ "Existing files there are read-only: add new test files, with new names.\n"
							+ $"Measured now:\n{DescribeCoverage(baseline, knownFailures)}\n"
						: string.Empty)
					+ (duplicationBefore is not null
						? $"\n{DuplicationMeasurement.Describe(duplicationBefore)}"
							+ "The same code appears in more than one file, so changing only this file cannot remove it. "
							+ $"You may edit this file and these files that share it: {string.Join(", ", duplicationBefore.PeerFiles)}. "
							+ "You may also add one new file in the same folder to hold the shared code. "
							+ "Joining lines, moving braces or renaming does not reduce duplication and will not be counted: "
							+ "put each repeated run in one place and use it from every copy.\n"
						: string.Empty),
				AiFixPrompt.SystemPrompt);

			recordedRequest = request;
			LogSessionStart(sessionLog, request, ollama, fixOptions, writablePaths: coverageMode ? testFolders : null);

			// Into the transcript before the first turn, so the pane reads in the order the model saw
			// things. Not through Say: the shared console is not the place for two pages of prompt.
			item.Transcript.Append(WorkLineKind.Prompt, $"System prompt:\n{request.SystemPrompt}");
			item.Transcript.Append(WorkLineKind.Prompt, $"Task:\n{request.Task}");

			var outcome = await session
				.RunAsync(
					request,
					token => coverageMode
						? CheckCoverageAsync(row, toolbox, knownFailures, status => coverageNow = status, token)
						: remotelyGraded
							? CheckLocalChangeAsync(
								row,
								toolbox,
								verifyTests ? testBaseline : null,
								token,
								duplicationBefore is null ? null : (duplicationBefore, targetPath!))
							: ReEvaluateAndBuildAsync(row, ruleId, toolbox, buildWasBroken, token),
					cancellationToken)
				.ConfigureAwait(false);

			succeeded = outcome.Succeeded;
			recordedOutcome = outcome;

			if (succeeded && remotelyGraded)
			{
				// Deliberately no ReplaceRuleResult: the rule still says what Codacy last told it, and
				// painting it green here would claim a verdict nothing has issued.
				Say($"✅ {string.Join(", ", toolbox.FilesWritten)} changed and the build still passes. "
					+ $"{ruleId} stays as it is until Codacy re-grades the merged branch.");
			}
			else if (succeeded)
			{
				// The one rule is re-evaluated rather than the whole repository: it is the only thing that
				// can have changed, and a full re-assessment here would cost far more than the fix did.
				var after = await ReEvaluateAsync(row, ruleId, cancellationToken).ConfigureAwait(false);
				ReplaceRuleResult(row, ruleId, after);

				Say($"✅ {outcome.Summary}");
			}

			if (succeeded)
			{
				// The model is finished, so its server is released before delivery: running the suite and
				// waiting for CI can take half an hour, and nobody else's fix should queue behind that.
				hold.Dispose();

				// Handed on rather than left for review: the tests, a pull request and CI are the evidence
				// now, and the pipeline names whichever gate stopped it.
				var delivered = await DeliverAiChangeAsync(
					row,
					AiChangeKind.RuleFix,
					$"rule {ruleId}",
					$"Fix {ruleId}: {result.RuleName}",
					toolbox.FilesWritten,
					new HashSet<string>(knownFailures.Concat(testBaseline), StringComparer.Ordinal),
					cancellationToken).ConfigureAwait(false);

				if (delivered.Kind == AiChangeOutcomeKind.Refused)
				{
					Say("↩️ The edits are still in the working tree: they were not delivered, and nothing else was touched.");
				}

				await dashboard.RefreshGitStatusAsync(row, CancellationToken.None).ConfigureAwait(false);
				cache.UpsertRow(row);
			}
		}
		catch (OperationCanceledException)
		{
			Say($"⏹️ Stopped {ruleId} on {row.RepositoryFullName}.");
			throw;
		}
		finally
		{
			// Taken before anything is put back: Undo forgets what the session wrote, and the diff of a
			// reverted change is exactly what a reviewer most wants to see.
			var writtenFiles = recordedToolbox?.FilesWritten.Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

			var sessionDiff = governance is not null && row.LocalPath is not null && writtenFiles.Count > 0
				? await governance.CaptureDiffAsync(row.LocalPath, writtenFiles, CancellationToken.None).ConfigureAwait(false)
				: string.Empty;

			string? keptProgress = null;

			if (!succeeded)
			{
				// Not on a stop the user asked for: that is a request to put things back as they were.
				keptProgress = progressWorthKeeping is not null && !cancellationToken.IsCancellationRequested
					? await progressWorthKeeping().ConfigureAwait(false)
					: null;

				if (keptProgress is not null)
				{
					Say($"📈 Coverage went from {keptProgress} — short of the target, but the tests compile and pass, "
						+ "so they are kept. Run it again to carry on from here.");
				}
				else
				{
					// Only what this session wrote, not everything the clone has changed. Discarding the lot
					// also destroys an earlier fix still waiting to be reviewed, which is how a failed run on
					// one rule came to threaten the successful run on another.
					var undone = undoWrites?.Invoke() ?? 0;

					Say(undone == 0
						? "↩️ Nothing this session wrote needed putting back."
						: $"↩️ Put back {undone} file(s) this session had written; other changes in the clone were left alone.");
				}

				await dashboard.RefreshGitStatusAsync(row, CancellationToken.None).ConfigureAwait(false);
			}

			if (governance is not null && recordedRequest is not null)
			{
				governance.RecordAiSession(
					item,
					recordedRequest,
					runtimeSettings.Ollama.Model,
					recordedOutcome,
					succeeded ? "applied"
						: cancellationToken.IsCancellationRequested ? "cancelled"
						: keptProgress is not null ? "kept"
						: "reverted",
					writtenFiles,
					sessionDiff,
					recordedLogPath);
			}
		}
	}

	/// <summary>
	/// The line coverage a TST-10 session aims for locally, a little above Codacy's 60% so that a small
	/// difference in what each side counts does not leave a "finished" session short of the real bar.
	/// </summary>
	private const double _coverageTarget = 62;

	/// <summary>Turns a coverage session gets per attempt: reading, writing and measuring is many steps.</summary>
	private const int _coverageTurnsPerAttempt = 30;

	/// <summary>How many points of coverage a failed session must have added for its tests to be kept.</summary>
	private const double _coverageProgressWorthKeeping = 2;

	/// <summary>
	/// How long any other AI session may run in all, checks included. The one that prompted this ran thirty-four
	/// minutes on a change the model made in two, nearly all of it waiting for tests that call a live service.
	/// </summary>
	private static readonly TimeSpan _sessionTimeBudget = TimeSpan.FromMinutes(20);

	/// <summary>How long a coverage session may run in all, whatever its turns and attempts allow.</summary>
	private static readonly TimeSpan _coverageTimeBudget = TimeSpan.FromMinutes(25);

	/// <summary>
	/// Builds the test project a just-written file belongs to, and says whether it compiled.
	/// </summary>
	/// <returns>Null for anything that is not C# or belongs to no test project.</returns>
	private static async Task<string?> BuildFeedbackAsync(
		string cloneRoot,
		IReadOnlyList<string> testProjects,
		string writtenPath,
		CancellationToken cancellationToken)
	{
		if (!writtenPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var project = testProjects.FirstOrDefault(candidate =>
			writtenPath.StartsWith((Path.GetDirectoryName(candidate) ?? string.Empty).Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase));

		if (project is null)
		{
			return null;
		}

		var (ok, text) = await LocalRepoService
			.RunCommandWithStreamingAsync(cloneRoot, "dotnet", ["build", project, "--nologo", "--verbosity", "quiet"], null, cancellationToken)
			.ConfigureAwait(false);

		if (ok)
		{
			return "It compiles.";
		}

		var errors = text.Split('\n')
			.Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
			.Select(line => line.Trim())
			.Distinct()
			.Take(12);

		return "IT DOES NOT COMPILE. Fix this before writing anything else:\n" + string.Join("\n", errors);
	}

	/// <summary>A coverage run as one phrase for the status line, for example "coverage 54.5% of 62%".</summary>
	private static string CoverageStatus(CoverageRun run)
		=> run.Report is null
			? "coverage not measurable"
			: $"coverage {run.Report.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}% of "
				+ $"{_coverageTarget.ToString("0", CultureInfo.InvariantCulture)}%";

	/// <summary>
	/// A coverage run as the model reads it: whether the tests passed, the figure, and where to look next.
	/// </summary>
	private static string DescribeCoverage(CoverageRun run, IReadOnlySet<string> knownFailures)
	{
		if (run.Report is null)
		{
			return $"Coverage could not be measured. {run.Problem}\n{run.OutputTail}".TrimEnd();
		}

		string tests;

		if (run.TestsPassed)
		{
			tests = "All tests passed.";
		}
		else if (OnlyKnownFailures(run, knownFailures))
		{
			// Said plainly, because a model shown a red test run will try to turn it green. These failed
			// before it wrote anything, for want of a credential or a service CI provides and this
			// sandbox deliberately does not — they are not its to fix, and not its to edit.
			tests = $"{run.FailedTests!.Count} test(s) fail, and every one of them failed before you started "
				+ "(they need a credential or a live service). Ignore them. Do not edit them.";
		}
		else
		{
			var added = (run.FailedTests ?? new HashSet<string>()).Where(name => !knownFailures.Contains(name)).Order().ToList();

			tests = (added.Count > 0
				? $"Tests that passed before now FAIL or are new and failing: {string.Join(", ", added)}. Fix those first.\n"
				: "The test run failed. Fix that first.\n")
				+ run.OutputTail;
		}

		return $"{tests}\n{run.Report.Describe(_coverageTarget)}";
	}

	/// <summary>
	/// Whether a failing test run failed only in tests that were already failing before the session.
	/// </summary>
	private static bool OnlyKnownFailures(CoverageRun run, IReadOnlySet<string> knownFailures)
		=> !run.TestsPassed
			&& run.Report is not null
			&& run.FailedTests is { Count: > 0 } failed
			&& failed.All(knownFailures.Contains);

	/// <summary>
	/// The success criterion for a TST-10 session: the tests pass, and the figure they produce is at the
	/// target.
	/// </summary>
	/// <remarks>
	/// A real measurement, so it can be failed with real information: how far short, and in which files,
	/// which is what the next attempt needs and what a rule that only says "no figure" never could.
	/// </remarks>
	private async Task<AiRuleCheck> CheckCoverageAsync(
		RepositoryDashboardRow row,
		AiFixToolbox toolbox,
		IReadOnlySet<string> knownFailures,
		Action<string> noteCoverage,
		CancellationToken cancellationToken)
	{
		if (toolbox.FilesWritten.Count == 0)
		{
			return new AiRuleCheck(false, "You have not written any test yet, so coverage has not changed.");
		}

		var run = await CoverageMeasurement.MeasureAsync(row.LocalPath!, null, cancellationToken).ConfigureAwait(false);
		noteCoverage(CoverageStatus(run));
		var testsFine = run.TestsPassed || OnlyKnownFailures(run, knownFailures);
		var passed = run.Report is not null && testsFine && run.Report.LinePercent >= _coverageTarget;

		Say(run.Report is null
			? $"   ⚠️ Coverage could not be measured: {run.Problem}"
			: $"   📏 {run.Report.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}% line coverage, "
				+ (run.TestsPassed ? "tests pass." : testsFine ? "only already-failing tests fail." : "tests FAIL."));

		return new AiRuleCheck(
			passed,
			passed
				? $"Local line coverage is {run.Report!.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}% and no test you could have affected fails."
				: DescribeCoverage(run, knownFailures));
	}

	/// <summary>
	/// How many diagnostic lines of a build or test run the model is shown.
	/// </summary>
	/// <remarks>
	/// Enough to fix what it broke, few enough to leave room for the task. A model that has caused
	/// forty errors has caused one error forty times.
	/// </remarks>
	private const int _diagnosticLineLimit = 30;

	/// <summary>
	/// Notes which files are already changed in the clone, and what they contain, so the Changes tab can
	/// label them pre-existing. Runs git off the calling thread. A baseline is only a label, so any
	/// failure leaves it empty rather than failing or delaying the fix.
	/// </summary>
	internal static async Task RecordPreExistingChangesAsync(WorkItem item, string clonePath, CancellationToken cancellationToken = default)
	{
		try
		{
			item.PreExistingChanges = await Task
				.Run(() => new CloneChangeReader().Baseline(clonePath), cancellationToken)
				.WaitAsync(cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// A cancelled fix stops now rather than after the baseline.
			throw;
		}
		catch (Exception)
		{
			item.PreExistingChanges = new Dictionary<string, string>(StringComparer.Ordinal);
		}
	}

	/// <summary>
	/// What a build or test run says, in a form worth sending to a model and worth keeping in a
	/// transcript.
	/// </summary>
	/// <remarks>
	/// Two problems, one cause. The run's output used to go straight to <see cref="Say"/>, and
	/// <c>dotnet build --verbosity normal</c> prints every compiler command line it issues — thousands
	/// of lines of reference paths. A transcript holds five hundred lines, so one build erased the
	/// whole session from the pane it was meant to be read in.
	/// <para>
	/// Meanwhile the model was handed <c>row.StatusMessage</c>, which is the words "Build failed." and
	/// nothing else — so a model that had just broken the build was told only that it had, and its next
	/// turn was a guess. The errors are the one part of that output it can act on, so the errors are
	/// what it gets.
	/// </para>
	/// </remarks>
	private static string SummariseRun(string what, bool succeeded, List<string> output)
	{
		var verdict = succeeded ? $"{what} succeeded." : $"{what} failed.";

		if (succeeded)
		{
			return verdict;
		}

		var diagnostics = output
			.Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase)
				|| (line.Contains("failed:", StringComparison.OrdinalIgnoreCase)
					// A summary line reading "failed: 0" is the opposite of a diagnostic, and listing it
					// under "Tests failed." told a model the run had failed for no reason it could find.
					&& !line.Contains("failed: 0", StringComparison.OrdinalIgnoreCase))
				|| line.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.Ordinal)
			.Take(_diagnosticLineLimit)
			.ToList();

		if (diagnostics.Count > 0)
		{
			return $"{verdict}\n{string.Join("\n", diagnostics)}";
		}

		// Nothing recognisable as an error: no test failed, so the run stopped for another reason — a build
		// that broke before any test started, a crash, no tests found. The end of the output is where
		// that reason is, and it is better to show it than to say there is nothing to show.
		var tail = output
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.TakeLast(_diagnosticLineLimit / 2)
			.ToList();

		return tail.Count == 0
			? $"{verdict} The run produced no output."
			: $"{verdict} No individual error was recognised. The end of the output was:\n{string.Join("\n", tail)}";
	}

	/// <summary>
	/// Whether the rule's verdict comes from a service reading the published branch.
	/// </summary>
	private static bool IsRemotelyGraded(string ruleId)
		=> PanoramicData.NugetManagement.Services.RuleRegistry.Rules
			.First(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
			is IRemotelyGraded;

	/// <summary>
	/// The success criterion for a rule that cannot be asked about the working tree: something was
	/// written, and the repository still builds.
	/// </summary>
	/// <remarks>
	/// A weaker claim than "the rule passes", and honestly so — nothing here can know whether Codacy
	/// will like the change. It is, though, the whole of what is knowable locally, and it separates the
	/// two failures that matter: a model that narrated for twelve turns and changed nothing, and one
	/// that changed something and broke the build. Both are caught; a plausible edit is kept and left
	/// for a person to read.
	/// <para>
	/// Tests are not run. A repository whose tests take ten minutes would spend them on every file of
	/// every Codacy fix, and a change that compiles is the bar this criterion is honest about clearing.
	/// </para>
	/// </remarks>
	/// <summary>
	/// The test classes that can have changed behaviour, when every file written is C# inside a test
	/// project; otherwise null, meaning run everything.
	/// </summary>
	/// <param name="cloneRoot">The repository clone.</param>
	/// <param name="written">The files written, relative to the clone.</param>
	internal static IReadOnlyList<string>? TestClassesAffectedBy(string cloneRoot, IReadOnlyCollection<string> written)
	{
		if (written.Count == 0 || written.Any(file => !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
		{
			return null;
		}

		var folders = CoverageMeasurement.FindTestProjects(cloneRoot)
			.Select(project => (Path.GetDirectoryName(project) ?? string.Empty).Replace('\\', '/'))
			.Where(folder => folder.Length > 0)
			.ToList();

		if (written.Any(file => !folders.Any(folder => file.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))))
		{
			return null;
		}

		var classes = written
			.Select(file => Path.Combine(cloneRoot, file))
			.Where(File.Exists)
			.SelectMany(path => CoverageMeasurement.ClassesDeclaredIn(File.ReadAllText(path)))
			.Distinct(StringComparer.Ordinal)
			.ToList();

		return classes.Count == 0 ? null : classes;
	}

	private async Task<AiRuleCheck> CheckLocalChangeAsync(
		RepositoryDashboardRow row,
		AiFixToolbox toolbox,
		IReadOnlySet<string>? testBaseline,
		CancellationToken cancellationToken,
		(DuplicationReport Before, string Target)? duplication = null)
	{
		if (toolbox.FilesWritten.Count == 0)
		{
			return new AiRuleCheck(false, "Nothing was written, so nothing changed.");
		}

		// Captured rather than said, for the reason SummariseRun gives: a verbose build streamed into
		// the transcript pushes the session out of it.
		var captured = new List<string>();
		await dashboard.BuildAsync(row, captured.Add, cancellationToken, restore: true).ConfigureAwait(false);

		var built = row.Status == PackageStatus.BuildSucceeded;

		RememberBuildResult(row, built ? RepositoryBuildState.Succeeded : RepositoryBuildState.Failed);

		var summary = SummariseRun("Build", built, captured);
		Say(built ? $"   ✓ {summary}" : $"   ⚠️ {summary}");

		if (!built)
		{
			return new AiRuleCheck(false, summary);
		}

		// A change that compiles can still break behaviour: refactoring duplicated tests to satisfy Codacy
		// once left a test failing, and a build-only check pushed it. So the tests run too, in the sandbox,
		// and only a test that passed before and fails now counts against the session — the ones already
		// failing here need a credential CI has and this run withholds.
		if (testBaseline is not null)
		{
			var testOutput = new List<string>();

			// When the session touched nothing but test source, only the classes in those files can have
			// changed behaviour, so only they are run. In a library whose tests call a live service the
			// whole suite takes five to twenty-five minutes and fails on its own account now and then; one
			// session was told that tests in an unrelated class had broken, and spent its last attempt on it.
			var scope = TestClassesAffectedBy(row.LocalPath!, toolbox.FilesWritten);

			if (scope is not null)
			{
				Say($"   🧪 Running only the classes in the changed test file(s): {string.Join(", ", scope)}.");
			}

			await dashboard.RunTestsAsync(row, testOutput.Add, cancellationToken, sandbox: true, testClasses: scope).ConfigureAwait(false);

			var broken = CoverageMeasurement.ParseFailedTests(string.Join('\n', testOutput))
				.Where(name => !testBaseline.Contains(name))
				.Order(StringComparer.Ordinal)
				.ToList();

			if (broken.Count > 0)
			{
				Say($"   ⚠️ {broken.Count.ToString(CultureInfo.InvariantCulture)} test(s) that passed before now fail.");

				return new AiRuleCheck(
					false,
					"Your change builds, but these tests passed before you started and fail now: "
					+ $"{string.Join(", ", broken.Take(10))}. Fix them without undoing what you changed.\n"
					+ SummariseRun("Tests", false, testOutput));
			}

			Say("   ✓ No test that passed before fails now.");
		}

		// Last, because it is the cheap check and the others are the ones that can fail for reasons the model can
		// fix quickly; but it is the one that says whether the change did anything about the grade.
		if (duplication is { } measure)
		{
			var after = await Task.Run(() => DuplicationMeasurement.Measure(row.LocalPath!, measure.Target), cancellationToken)
				.ConfigureAwait(false);

			if (after is not null)
			{
				Say($"   📏 Repeated code in {measure.Target}: "
					+ $"{measure.Before.Percent.ToString("0", CultureInfo.InvariantCulture)}% → "
					+ $"{after.Percent.ToString("0", CultureInfo.InvariantCulture)}%.");

				if (!DuplicationMeasurement.IsMeaningfulImprovement(measure.Before, after))
				{
					return new AiRuleCheck(
						false,
						$"The build passes, but {measure.Target} is still {after.Percent.ToString("0", CultureInfo.InvariantCulture)}% "
						+ $"repeated (it was {measure.Before.Percent.ToString("0", CultureInfo.InvariantCulture)}%). "
						+ "Reformatting or joining lines does not change that: it is the same code in more than one place. "
						+ "Put a repeated run in one shared place — a base interface, a helper method, a new file in the "
						+ "same folder — and use it from each copy.\n"
						+ DuplicationMeasurement.Describe(after));
				}
			}
		}

		return new AiRuleCheck(true, $"{toolbox.FilesWritten.Count} file(s) changed and the build succeeded.");
	}

	/// <summary>
	/// The rule's verdict, and — when the rule is satisfied by something that can break compilation —
	/// whether the repository still builds.
	/// </summary>
	/// <remarks>
	/// A rule that reads project or package files passes the moment the text is right, whatever that did
	/// to the code. The first session to exercise this swapped a package reference, satisfied PKG-12, and
	/// left the test project unable to compile; the next session's build check then failed for a reason
	/// it had no part in. Making the build part of the answer sends that failure back to the session
	/// that caused it, with the compiler's errors, while it is still in the room.
	/// </remarks>
	private async Task<AiRuleCheck> ReEvaluateAndBuildAsync(
		RepositoryDashboardRow row,
		string ruleId,
		AiFixToolbox toolbox,
		bool buildWasBroken,
		CancellationToken cancellationToken)
	{
		var check = await ReEvaluateAsync(row, ruleId, cancellationToken).ConfigureAwait(false);

		if (!check.Passed || buildWasBroken || toolbox.FilesWritten.Count == 0)
		{
			return check;
		}

		var captured = new List<string>();
		await dashboard.BuildAsync(row, captured.Add, cancellationToken, restore: true).ConfigureAwait(false);

		var built = row.Status == PackageStatus.BuildSucceeded;

		RememberBuildResult(row, built ? RepositoryBuildState.Succeeded : RepositoryBuildState.Failed);

		if (built)
		{
			return check;
		}

		var summary = SummariseRun("Build", false, captured);
		Say($"   ⚠️ {summary}");

		return new AiRuleCheck(
			false,
			"The rule is now satisfied, but your change stopped the repository building. Fix the build "
			+ $"without undoing what satisfied the rule.\n{summary}");
	}

	/// <summary>
	/// Evaluates one rule against the repository's clone as it now stands.
	/// </summary>
	/// <remarks>
	/// A fresh context each time. A cached one would report the repository as it was before the model
	/// edited it, so every attempt would look like a failure and the loop would exhaust its retries
	/// against its own stale reading.
	/// </remarks>
	private async Task<AiRuleCheck> ReEvaluateAsync(
		RepositoryDashboardRow row,
		string ruleId,
		CancellationToken cancellationToken)
	{
		var github = await CreateGitHubClientAsync().ConfigureAwait(false);
		var context = await dashboard.BuildContextAsync(row, github, cancellationToken).ConfigureAwait(false);

		var rule = PanoramicData.NugetManagement.Services.RuleRegistry.Rules
			.First(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

		var result = await rule.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);

		return new AiRuleCheck(result.Passed, result.Message);
	}

	/// <summary>
	/// Swaps one rule's result in the cached assessment, so the tree reflects the fix without a full
	/// re-assessment.
	/// </summary>
	private static void ReplaceRuleResult(RepositoryDashboardRow row, string ruleId, AiRuleCheck check)
	{
		if (row.Assessment is null)
		{
			return;
		}

		var index = row.Assessment.RuleResults.FindIndex(r =>
			string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

		if (index < 0)
		{
			return;
		}

		var existing = row.Assessment.RuleResults[index];

		row.Assessment.RuleResults[index] = new PanoramicData.NugetManagement.Models.RuleResult
		{
			RuleId = existing.RuleId,
			RuleName = existing.RuleName,
			Category = existing.Category,
			Severity = existing.Severity,
			IsApplicable = existing.IsApplicable,
			Passed = check.Passed,
			Message = check.Message,
			Advisory = check.Passed ? null : existing.Advisory
		};
	}

	/// <summary>
	/// Applies the auto-remediation of one rule to a repository, then re-assesses it.
	/// </summary>
	/// <remarks>
	/// The rule is looked up on the row's current assessment rather than trusted from when it was
	/// queued: the queue can take minutes to reach an item, by which time the rule may already have
	/// been fixed by something else, in which case the item is skipped rather than re-applied.
	/// </remarks>
	/// <param name="item">The item naming the repository and, via <c>ruleId</c>, which rule to fix.</param>
	/// <param name="progress">Unused: a single-rule fix reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task FixRuleAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var ruleId = item.Descriptor.Parameter("ruleId")!;
		var result = row.Assessment?.RuleResults
			.FirstOrDefault(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

		if (result is null || result.Passed)
		{
			logger.LogWarning(
				"Skipping {RuleId} on {Repository}: it is no longer failing.",
				ruleId,
				row.RepositoryFullName);
			return;
		}

		if (row.LocalPath is null)
		{
			Say($"⏭️ {row.RepositoryFullName} is no longer cloned locally — skipping {result.RuleId}.");
			return;
		}

		Say($"▶ Fixing {result.RuleId}...");

		// Whether the write phase finished. A single remediation is applied synchronously and cannot
		// be interrupted part-way, so in this executor the flag is only ever false for a Stop observed
		// before the call — but it is tracked the same way as its two siblings so the three do not
		// diverge in behaviour that is this easy to get wrong.
		var remediationCompleted = false;

		try
		{
			var applied = await dashboard
				.ApplyRuleRemediationAsync(row, result, Say, cancellationToken)
				.ConfigureAwait(false);
			remediationCompleted = true;
			governance?.RecordRemediation(item, row.RepositoryFullName, result.RuleId, applied);

			await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
			cache.UpsertRow(row);
			Say(applied.Count > 0 ? $"✅ Fixed {result.RuleId}" : $"⚠️ Could not fix {result.RuleId}");

			Say("▶ Re-assessing...");

			// The client is what lets the local assessment read the repository's inbox, so a
			// re-assessment after a fix reports the same issue state a fresh one would.
			await dashboard.AssessLocalRepositoryAsync(
				row,
				cancellationToken,
				await CreateGitHubClientAsync().ConfigureAwait(false)).ConfigureAwait(false);
			cache.UpsertRow(row);
		}
		catch (OperationCanceledException)
		{
			// Atomic over the WRITE phase only. A remediation stopped part-way is undone, so the clone
			// is left as it was found rather than carrying half a fix into the next commit — but once
			// the remediation has landed, the re-assessment that follows only reads the clone, and
			// throwing away a fix the user asked for because a subsequent read was interrupted would
			// destroy work they never asked to undo.
			await RevertIfRemediationIncompleteAsync(row, remediationCompleted).ConfigureAwait(false);
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Undoes a stopped fix, but only when the remediation itself did not finish.
	/// </summary>
	/// <remarks>
	/// The atomicity a stopped fix owes the user is over the write phase, not over the whole executor.
	/// Remediation writes to the clone; the git-status refresh and the re-assessment that follow it
	/// only read. Reverting after the remediation has landed would discard a fix the user explicitly
	/// asked for because a subsequent read was interrupted — destroying work they never asked to undo,
	/// on a path they reached by pressing Stop to save time.
	/// <para>
	/// The stop is still reported either way, so the console never goes quiet on a Stop; only the
	/// discard is conditional.
	/// </para>
	/// </remarks>
	/// <param name="row">The repository that was being fixed.</param>
	/// <param name="remediationCompleted">
	/// Whether the write phase ran to completion. False means the clone may hold a partly-written fix.
	/// </param>
	private async Task RevertIfRemediationIncompleteAsync(RepositoryDashboardRow row, bool remediationCompleted)
	{
		if (remediationCompleted)
		{
			Say("⏹️ Stopped after the remediation had been applied — the applied changes are kept.");
			return;
		}

		await RevertPartAppliedFixAsync(row).ConfigureAwait(false);
	}

	/// <summary>
	/// Reads one human-raised issue and records what should happen about it.
	/// </summary>
	/// <remarks>
	/// The whole of this application's exposure to text a stranger wrote lives in this method, and the
	/// containment is that the session it starts holds no tools. Nothing read here can cause a file to
	/// be opened, a comment to be posted or a request to leave the machine — the only thing that comes
	/// back is a verdict, which is parsed into closed types and then gated in C#.
	/// <para>
	/// What follows the verdict is deliberately asymmetric. A fix that clears the gate is queued, an
	/// answer that clears it is posted, and a rejection is never carried out here at any confidence:
	/// closing somebody's issue is published under the organisation's name and waits for a person.
	/// </para>
	/// </remarks>
	private async Task AnalyseIssueAsync(
		WorkItem item,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		var row = RowFor(item);

		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var issueNumber = int.Parse(item.Descriptor.Parameter("issueNumber")!, CultureInfo.InvariantCulture);
		var ollama = runtimeSettings.Ollama;

		if (!ollama.IsConfigured)
		{
			Say("⚠️ No Ollama server is configured — set one under Settings, Ollama Config.");
			return;
		}

		var issue = row.OpenIssues.FirstOrDefault(candidate => candidate.Number == issueNumber);

		if (issue is null)
		{
			Say($"⏭️ Issue #{issueNumber} is no longer open on {row.RepositoryFullName}.");
			return;
		}

		// Re-checked rather than trusted from the queue: an item can sit in a lane while the world
		// changes, and analysing a bot's issue would have two passes judging the same item in public.
		if (!HumanIssueFilter.IsHumanRaised(issue))
		{
			Say($"⏭️ #{issueNumber} is not a human-raised issue.");
			return;
		}

		var github = await CreateGitHubClientAsync().ConfigureAwait(false);
		var issueApi = new OctokitGitHubIssueApi(github);
		var (owner, name) = SplitFullName(row.RepositoryFullName);

		var thread = await issueApi
			.GetThreadAsync(owner, name, issueNumber, cancellationToken)
			.ConfigureAwait(false);

		var input = new IssueAnalysisInput(
			row.RepositoryFullName,
			thread.Number,
			thread.Title,
			thread.Body,
			thread.AuthorLogin,
			thread.AuthorAssociation,
			thread.CreatedAtUtc,
			issue.LastMaintainerReplyUtc,
			[.. thread.Comments.Select(comment => new IssueAnalysisComment(
				comment.AuthorLogin, comment.AuthorAssociation, comment.CreatedAtUtc, comment.Body))],
			RepositoryFileList(row),
			[.. row.Assessment?.RuleResults.Where(result => !result.Passed).Select(result => result.RuleId)
				?? []]);

		Say($"⏳ Waiting for the model ({ollama.Model})...");
		using var hold = await ollamaGate.EnterAsync(cancellationToken).ConfigureAwait(false);

		using var client = new Ollama.Api.OllamaClient(new Ollama.Api.OllamaClientOptions
		{
			Uri = new Uri(ollama.BaseUrl!),
			ApiKey = ollama.ApiKey,
			Timeout = TimeSpan.FromMilliseconds(ollama.RequestTimeoutMs)
		});

		var session = new IssueAnalysisSession(
			new OllamaChatModel(client, ollama.Model!, ollama.ContextWindow),
			Say,
			AiTranscriptSink.For(item.Transcript));

		item.Transcript.Append(WorkLineKind.Prompt, $"System prompt:\n{IssueAnalysisSession.SystemPrompt}");
		item.Transcript.Append(
			WorkLineKind.Prompt,
			$"Task:\n{IssueAnalysisSession.BuildTask(input, DateTimeOffset.UtcNow)}");

		var verdict = await session
			.AnalyseAsync(input, DateTimeOffset.UtcNow, cancellationToken)
			.ConfigureAwait(false);

		issue.Analysis = verdict;
		issue.AnalysedAtIssueUpdatedUtc = thread.Comments.Count > 0
			? thread.Comments[^1].CreatedAtUtc
			: thread.CreatedAtUtc;

		cache.UpsertRow(row);

		await ActOnVerdictAsync(item, row, issue, thread, verdict, github, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Does whatever the gate permits about a verdict, and says what it left for a human.
	/// </summary>
	private async Task ActOnVerdictAsync(
		WorkItem item,
		RepositoryDashboardRow row,
		RepositoryIssue issue,
		GitHubIssueThread thread,
		IssueVerdict verdict,
		IGitHubClient github,
		CancellationToken cancellationToken)
	{
		if (verdict.Action is IssueAction.Fix)
		{
			var decision = IssueAutonomyGate.MayFixAutomatically(
				verdict,
				thread.AuthorAssociation,
				row.LocalPath ?? string.Empty,
				RemediationOwnedPaths(row));

			if (decision.Allowed)
			{
				fanOut.EnqueueIssueFix(
					row.Organization, row.RepositoryFullName, issue.Number, item.ConsoleNodeKey);

				Say($"▶ #{issue.Number}: queued a fix. {decision.Reason}");
				return;
			}

			Say($"⏸️ #{issue.Number}: a fix is proposed but waits for you — {decision.Reason}");
			return;
		}

		if (verdict.Action is IssueAction.Answer)
		{
			var decision = IssueAutonomyGate.MayAnswerAutomatically(verdict);

			if (decision.Allowed)
			{
				var (owner, name) = SplitFullName(row.RepositoryFullName);

				await new OctokitGitHubWriteApi(github)
					.CommentAsync(owner, name, issue.Number, verdict.DraftReply!, cancellationToken)
					.ConfigureAwait(false);

				Say($"💬 #{issue.Number}: replied. {decision.Reason}");
				return;
			}

			Say($"⏸️ #{issue.Number}: a reply is drafted but waits for you — {decision.Reason}");
			return;
		}

		// Reject and Escalate both stop here, and Reject stops here however sure the model was.
		// Closing a real person's issue — or saying in public that it looked like an attack — is not
		// worth automating, and a rejection is also exactly what somebody would aim for who wanted a
		// genuine report closed unread.
		Say(verdict.Action is IssueAction.Reject
			? $"⏸️ #{issue.Number}: a rejection is drafted and waits for you. Rejections are never posted "
				+ "automatically."
			: $"⏸️ #{issue.Number}: left for you. {verdict.Reasoning}");
	}

	/// <summary>
	/// Acts on the brief one analysis produced, seeing the brief and never the issue.
	/// </summary>
	/// <remarks>
	/// Two things make this different from a rule-driven fix. The session may write only the files the
	/// brief named, because there is no rule to notice it wandering; and a failed attempt is undone,
	/// because a rule-driven mess announces itself at the next assessment while this one would not.
	/// </remarks>
	private async Task FixWithAiIssueAsync(
		WorkItem item,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		var row = RowFor(item);

		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var issueNumber = int.Parse(item.Descriptor.Parameter("issueNumber")!, CultureInfo.InvariantCulture);
		var ollama = runtimeSettings.Ollama;

		if (!ollama.IsConfigured)
		{
			Say("⚠️ No Ollama server is configured — set one under Settings, Ollama Config.");
			return;
		}

		if (row.LocalPath is null || !row.IsClonedLocally)
		{
			Say($"⏭️ {row.RepositoryFullName} is not cloned locally, and the model edits files on disk.");
			return;
		}

		var issue = row.OpenIssues.FirstOrDefault(candidate => candidate.Number == issueNumber);
		var brief = issue?.Analysis?.Brief;

		if (issue is null || brief is null)
		{
			Say($"⏭️ #{issueNumber} has no fix brief any more.");
			return;
		}

		// Screened again here, and not only at the gate. The clone moves — another session commits, a
		// remediation rewrites a file, a branch changes underneath — and the allowlist this session is
		// about to be handed must describe the repository as it is now, not as it was when a model
		// looked at it.
		var screen = IssuePathScreen.Screen(row.LocalPath, brief.Paths, RemediationOwnedPaths(row));

		if (!screen.Accepted)
		{
			Say($"⏭️ #{issueNumber}: {screen.Refusal}");
			return;
		}

		// The reproducing test goes in the test project, and only there: the red-test step may add new test
		// files and nothing else, so a bug cannot be "reproduced" by editing the code it is meant to
		// exercise. A repository with no test project has nowhere to put one; it gets a project from the
		// TST-01 rule fix first, which is told how this estate builds one.
		var testProjects = CoverageMeasurement.FindTestProjects(row.LocalPath);

		if (testProjects.Count == 0)
		{
			Say($"⏭️ #{issueNumber}: {row.RepositoryFullName} has no test project, so there is nowhere to write a "
				+ "failing test. Fix TST-01 first.");
			return;
		}

		var testFolders = testProjects
			.Select(project => (Path.GetDirectoryName(project) ?? string.Empty).Replace('\\', '/'))
			.Where(folder => folder.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		try
		{
			using var client = new Ollama.Api.OllamaClient(new Ollama.Api.OllamaClientOptions
			{
				Uri = new Uri(ollama.BaseUrl!),
				ApiKey = ollama.ApiKey,
				Timeout = TimeSpan.FromMilliseconds(ollama.RequestTimeoutMs)
			});

			await RecordPreExistingChangesAsync(item, row.LocalPath, cancellationToken).ConfigureAwait(false);

			Func<CancellationToken, Task<string>> buildTool = async token =>
			{
				var captured = new List<string>();
				await dashboard.BuildAsync(row, captured.Add, token, restore: true).ConfigureAwait(false);
				return SummariseRun("Build", row.Status == PackageStatus.BuildSucceeded, captured);
			};

			Func<CancellationToken, Task<string>> testTool = async token =>
			{
				var captured = new List<string>();
				await dashboard.RunTestsAsync(row, captured.Add, token, sandbox: true).ConfigureAwait(false);
				return SummariseRun("Tests", row.Status == PackageStatus.TestsPassed, captured);
			};

			// Two toolboxes because the two steps may write different things: the first only new files in
			// the test folders, the second only the files the brief named.
			var redToolbox = new AiFixToolbox(
				row.LocalPath,
				build: buildTool,
				test: testTool,
				writableFolders: testFolders,
				newFilesOnly: true,
				afterWrite: (path, token) => BuildFeedbackAsync(row.LocalPath, testProjects, path, token));

			var fixToolbox = new AiFixToolbox(
				row.LocalPath,
				build: buildTool,
				test: testTool,
				writablePaths: brief.Paths);

			var oracle = new IssueFixOracle(
				async token =>
				{
					var captured = new List<string>();
					await dashboard.BuildAsync(row, captured.Add, token, restore: true).ConfigureAwait(false);
					return new IssueFixCheckStep(
						row.Status == PackageStatus.BuildSucceeded, string.Join("\n", captured));
				},
				async token =>
				{
					var captured = new List<string>();
					await dashboard.RunTestsAsync(row, captured.Add, token, sandbox: true).ConfigureAwait(false);
					return new IssueFixCheckStep(
						row.Status == PackageStatus.TestsPassed, string.Join("\n", captured));
				});

			async Task<AiRuleCheck> CheckTestCompilesAsync(CancellationToken token)
			{
				var captured = new List<string>();
				await dashboard.BuildAsync(row, captured.Add, token, restore: true).ConfigureAwait(false);
				var built = row.Status == PackageStatus.BuildSucceeded;

				return new AiRuleCheck(built, built ? "The test project builds." : string.Join('\n', captured.TakeLast(30)));
			}

			var fixOptions = new AiFixOptions
			{
				MaxTurnsPerAttempt = ollama.MaxTurnsPerAttempt,
				MaxAttempts = ollama.MaxAttemptsPerRule
			};

			using var redLog = OpenSessionLog(row.RepositoryFullName, $"issue-{issueNumber}-red");
			using var fixLog = OpenSessionLog(row.RepositoryFullName, $"issue-{issueNumber}-fix");

			AiFixSession NewSession(AiFixToolbox toolbox, AiSessionLog log) => new(
				new OllamaChatModel(client, ollama.Model!, ollama.ContextWindow, ollama.ToDirect()),
				toolbox,
				fixOptions,
				Say,
				AiTranscriptSink.For(item.Transcript),
				log,
				onStatus: progress.Report);

			// The report as the model sees it: the brief's paraphrase, never the reporter's own words.
			var issueText = $"Goal: {brief.Goal}\nDone looks like: {brief.ExpectedEndState}";

			var (owner, name) = SplitFullName(row.RepositoryFullName);

			var change = new AiChangeRequest(
				owner,
				name,
				AiChangeKind.IssueFix,
				AiChangePipelineFactory.Slug($"issue {issueNumber}", Guid.NewGuid().ToString("N")[..6]),
				$"Fix #{issueNumber}: {AiChangePipelineFactory.Shorten(brief.Goal, 60)}",
				issueNumber,
				RequiresRedTest: IssueAutonomyGate.RequiresRedTest);

			var fixPrompt = IssueFixPrompt.BuildTask(brief, row.RepositoryFullName, issueNumber);

			LogSessionStart(
				fixLog,
				new AiFixRequest(row.RepositoryFullName, $"issue-{issueNumber}", $"Issue #{issueNumber}", fixPrompt, AiFixPrompt.SystemPrompt),
				ollama,
				fixOptions,
				brief.Paths);

			item.Transcript.Append(WorkLineKind.Prompt, $"System prompt:\n{AiFixPrompt.SystemPrompt}");
			item.Transcript.Append(WorkLineKind.Prompt, $"Task:\n{fixPrompt}");

			Say($"▶ #{issueNumber} on {row.RepositoryFullName} via {ollama.Model}: a failing test first in "
				+ $"{string.Join(", ", testFolders)}, then a fix writing only {string.Join(", ", brief.Paths)}.");

			// The model's server is held by the decorator around each call into the model, and not by this
			// method: delivery waits for CI, which can take half an hour, and that must not idle the GPU.
			var work = new GatedAiChangeWork(
				new AiSessionChangeWork(
					NewSession(redToolbox, redLog),
					NewSession(fixToolbox, fixLog),
					issueText,
					fixPrompt,
					AiFixPrompt.SystemPrompt,
					oracle.CheckAsync,
					CheckTestCompilesAsync),
				ollamaGate);

			var pipeline = AiChangePipelineFactory.Create(
				new LocalRepoAiChangeGit(row.LocalPath, row.RepositoryFullName),
				new DashboardAiChangeTests(dashboard, localRepo, row, row.RepositoryFullName),
				work,
				new OctokitGitHubWriteApi(await CreateGitHubClientAsync().ConfigureAwait(false)),
				AiChangePipelineFactory.IssueFixLimits(brief.Paths, testFolders));

			await pipeline.RunAsync(change, Say, cancellationToken).ConfigureAwait(false);

			await dashboard.RefreshGitStatusAsync(row, CancellationToken.None).ConfigureAwait(false);
			cache.UpsertRow(row);
		}
		catch (OperationCanceledException)
		{
			Say($"⏹️ Stopped the fix for #{issueNumber}.");
			throw;
		}
	}

	/// <summary>
	/// The repository's file paths, for an analysis that is given paths and never contents.
	/// </summary>
	private static IReadOnlyList<string> RepositoryFileList(RepositoryDashboardRow row)
	{
		if (row.LocalPath is null || !Directory.Exists(row.LocalPath))
		{
			return [];
		}

		var root = Path.GetFullPath(row.LocalPath);

		return
		[
			.. Directory
				.EnumerateFiles(root, "*", SearchOption.AllDirectories)
				.Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
						StringComparison.OrdinalIgnoreCase)
					&& !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
						StringComparison.OrdinalIgnoreCase)
					&& !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
						StringComparison.OrdinalIgnoreCase))
				.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
				.Order(StringComparer.OrdinalIgnoreCase)
				.Take(IssueAnalysisSession.MaxFilesListed)
		];
	}

	/// <summary>
	/// The files a deterministic remediation already maintains, which an issue fix may not also write.
	/// </summary>
	/// <remarks>
	/// Two writers for one file means a rule and an issue undoing each other's work on alternate runs,
	/// and the rule is the one with a specification behind it.
	/// </remarks>
	private IReadOnlyCollection<string> RemediationOwnedPaths(RepositoryDashboardRow row)
		=> [.. (row.Assessment?.RuleResults ?? [])
			.Where(result => !result.Passed && remediations.Get(result.RuleId) is not null)
			.SelectMany(result => result.Advisory?.Targets?.Select(target => target.Path) ?? [])
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)];

	private static (string Owner, string Name) SplitFullName(string repositoryFullName)
	{
		var parts = repositoryFullName.Split('/');
		return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
	}

	/// <summary>
	/// Discards whatever a stopped fix had half-written and reports what came of it.
	/// </summary>
	/// <param name="row">The repository whose part-applied changes to discard.</param>
	private async Task RevertPartAppliedFixAsync(RepositoryDashboardRow row)
	{
		var (success, discarded) = await localRepo.DiscardLocalChangesAsync(row.RepositoryFullName, CancellationToken.None).ConfigureAwait(false);
		Say(success
			? discarded.Count == 0
				? "↩️ Stopped before anything was written."
				: $"↩️ Reverted {discarded.Count} change(s) written before the stop."
			: "⚠️ Could not revert the part-applied changes — check the clone by hand.");

		await dashboard.RefreshGitStatusAsync(row, CancellationToken.None).ConfigureAwait(false);
	}

	/// <summary>
	/// Classifies one repository's open Dependabot pull requests and acts on the verdicts.
	/// </summary>
	/// <remarks>
	/// The only work that mutates GitHub. Every intended comment and close is said before it is made,
	/// so this item's output is the audit trail for it.
	/// <para>
	/// Needs a current assessment, because coverage is decided from which rules are failing. Without
	/// one it says so and stops, rather than guessing that nothing is failing and closing on that
	/// basis — an unassessed repository would otherwise look like one where nothing is covered, and
	/// raise a gap issue for every dependency Dependabot had ever mentioned.
	/// </para>
	/// </remarks>
	/// <param name="item">The item naming the repository to triage.</param>
	/// <param name="progress">Unused: triage reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task TriageDependabotAsync(
		WorkItem item,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		var row = RowFor(item);

		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		if (row.Assessment is null)
		{
			Say($"⚠️ {row.RepositoryFullName} has no assessment yet — assess it first, then triage.");
			return;
		}

		if (!row.OpenIssuesKnown)
		{
			Say($"⚠️ {row.RepositoryFullName}'s open pull requests are not known — re-assess it first.");
			return;
		}

		var dependabotPullRequests = row.OpenIssues
			.Where(issue => issue.IsPullRequest)
			.ToList();

		if (dependabotPullRequests.Count == 0)
		{
			Say($"✅ {row.RepositoryFullName} has no open pull requests to triage.");
			return;
		}

		Say($"▶ Triaging {dependabotPullRequests.Count} open pull request(s) for {row.RepositoryFullName}...");

		var github = await CreateGitHubClientAsync().ConfigureAwait(false);
		var context = await dashboard
			.BuildContextAsync(row, github, cancellationToken)
			.ConfigureAwait(false);

		var triages = new DependabotTriageService().Triage(
			dependabotPullRequests,
			context,
			row.Assessment.RuleResults,
			ruleId => remediations.Get(ruleId) is not null);

		// Adoption writes files, which triage has never done. No clone means no adoption: the verdicts
		// are still reached and reported, and every adoptable pull request is left open saying why. The
		// guard matches the one the Fix lane uses, rather than inventing a second opinion about when a
		// clone can be written to.
		var adopter = row is { IsClonedLocally: true, LocalPath: not null }
			? new CloneBumpAdopter(row.LocalPath)
			: null;

		if (adopter is null)
		{
			Say($"ℹ️ {row.RepositoryFullName} is not cloned locally, so nothing can be adopted this pass.");
		}

		var outcome = await triageRunner
			.RunAsync(
				new OctokitGitHubIssueApi(github),
				new OctokitGitHubWriteApi(github),
				row.RepositoryFullName,
				triages,
				Say,
				cancellationToken,
				adopter)
			.ConfigureAwait(false);

		Say($"✅ {row.RepositoryFullName}: closed {outcome.Closed}, adopted {outcome.Adopted}, "
			+ $"closed {outcome.Obsolete} for dependencies it no longer references, "
			+ $"{outcome.Covered} awaiting an existing fix, {outcome.Uncovered} with no fix available, "
			+ $"{outcome.Unrecognised} left alone.");

		if (outcome.Adopted > 0)
		{
			// The adopted versions are sitting uncommitted in the clone. Saying so here is the only
			// warning anybody gets: the pull requests are already closed, so an unpushed clone is how
			// the bumps get lost.
			Say($"⚠️ {outcome.Adopted} adopted bump(s) are uncommitted in {row.LocalPath} — "
				+ "commit and push them, or the closed pull requests take the changes with them.");

			await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
		}

		// The closed ones have left the open list, and the survivors now carry their verdicts.
		row.OpenIssues = [.. DependabotTriageRunner.Restamp(row.OpenIssues, triages)];

		cache.UpsertRow(row);
	}

	/// <summary>
	/// Adopts Dependabot bumps into one repository's local clone.
	/// </summary>
	/// <param name="localPath">The root of the clone.</param>
	/// <remarks>
	/// The whole of the runner's file-system dependency, so the runner itself keeps no knowledge of
	/// working trees and stays testable without one.
	/// </remarks>
	private sealed class CloneBumpAdopter(string localPath) : IBumpAdopter
	{
		/// <inheritdoc />
		public IReadOnlyList<string> Adopt(DependabotAdoptionPlan plan, Action<string> onOutput)
			=> DependabotAdoptionRemediation.Adopt(localPath, plan, onOutput);
	}

	/// <summary>
	/// The cached row for an item's repository, or null when the repository is no longer known —
	/// which happens when a restored item names a repository since removed from the estate.
	/// </summary>
	/// <param name="item">The item whose repository is wanted.</param>
	private RepositoryDashboardRow? RowFor(WorkItem item)
		=> cache.GetCachedRows()?.FirstOrDefault(r => string.Equals(
			r.RepositoryFullName, item.RepositoryFullName, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Runs one repository's tests, and on failure leaves behind the AI prompt for the failure.
	/// </summary>
	/// <remarks>
	/// Like the build, the prompt is stored rather than pushed at a browser, and quotes only the
	/// lines this run produced.
	/// </remarks>
	/// <param name="item">The item naming the repository to test.</param>
	/// <param name="progress">Unused: a test run reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task TestAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var output = new List<string>();
		Say("▶ Running tests...");

		try
		{
			await dashboard.RunTestsAsync(
				row,
				line =>
				{
					output.Add(line);
					Say(line);
				},
				cancellationToken).ConfigureAwait(false);

			var passed = row.Status == PackageStatus.TestsPassed;

			if (passed)
			{
				Say("✅ Tests passed");
				item.Succeeded = true;
			}
			else
			{
				Say("❌ Tests failed");
				item.GeneratedPrompt = DashboardService.GenerateConciseWorkflowFailurePrompt(row, "test", output);
				item.Succeeded = false;
			}

			RememberTestResult(row, passed ? RepositoryTestState.Passed : RepositoryTestState.Failed);
		}
		catch (OperationCanceledException)
		{
			// Stopping is not failing. Rethrown so the runner marks the item Cancelled and logs
			// the stop, and so the step badge is left alone rather than painted red for work the
			// user chose to end.
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
			item.Succeeded = false;

			// Tests that could not be run are a repository whose tests do not pass here, which is what
			// the badge is asked. Leaving it as not-known would hide it among the never-tested.
			RememberTestResult(row, RepositoryTestState.Failed);
		}
	}

	/// <summary>What a build and test of the uncommitted changes concluded.</summary>
	/// <param name="Passed">Whether the changes are safe to commit.</param>
	/// <param name="Summary">What to tell the model, or the user, about it.</param>
	private sealed record UnstickCheck(bool Passed, string Summary);

	/// <summary>
	/// Gets one clone out from behind uncommitted changes that stop it syncing.
	/// </summary>
	/// <remarks>
	/// Order matters, and each step is the cheapest thing that could end the problem. The changes are built
	/// and tested as they stand, because most are fine and only needed committing. Only if they are not does
	/// the local model get involved, confined to the changed files, with a way to put a file back. And only
	/// if the model cannot manage either is the work stashed — set aside, never discarded — which is what
	/// lets the clone sync at all. Nothing here can lose work: the worst outcome is a line in
	/// <c>git stash list</c>.
	/// <para>
	/// A test that fails on the changed tree only counts against the changes if it passed on the committed
	/// one, and that baseline is measured lazily: it costs a second test run, which is minutes in a library
	/// whose tests call a live service, so it is paid only when something fails.
	/// </para>
	/// </remarks>
	/// <param name="item">The item naming the repository to unstick.</param>
	/// <param name="progress">Reports the model's progress while it works.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task UnstickWithAiAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		if (row.LocalPath is null || !row.IsClonedLocally)
		{
			Say($"⏭️ {row.RepositoryFullName} is not cloned locally, so there is nothing to unstick.");
			return;
		}

		try
		{
			await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);

			if (row.IsWorkingTreeClean != false)
			{
				Say("✅ The working tree is already clean.");
				item.Succeeded = await SyncAfterUnstickAsync(row, cancellationToken).ConfigureAwait(false);
				return;
			}

			var changed = await dashboard
				.GetWorkingTreeStatusPreviewAsync(row, int.MaxValue, cancellationToken)
				.ConfigureAwait(false);

			Say($"▶ {changed.Count} uncommitted change(s) in {row.RepositoryFullName} are stopping it syncing:");
			foreach (var line in changed.Take(12))
			{
				Say($"  {line}");
			}

			if (changed.Count > 12)
			{
				Say($"  ...and {changed.Count - 12} more.");
			}

			IReadOnlySet<string>? baseline = null;

			async Task<UnstickCheck> JudgeAsync(CancellationToken token)
			{
				var buildOutput = new List<string>();
				await dashboard.BuildAsync(row, buildOutput.Add, token, restore: true).ConfigureAwait(false);

				var built = row.Status == PackageStatus.BuildSucceeded;
				RememberBuildResult(row, built ? RepositoryBuildState.Succeeded : RepositoryBuildState.Failed);

				if (!built)
				{
					return new UnstickCheck(false, SummariseRun("Build", false, buildOutput));
				}

				var testOutput = new List<string>();
				await dashboard.RunTestsAsync(row, testOutput.Add, token, sandbox: true).ConfigureAwait(false);

				if (row.Status == PackageStatus.TestsPassed)
				{
					return new UnstickCheck(true, "The build succeeds and the tests pass.");
				}

				var failed = CoverageMeasurement.ParseFailedTests(string.Join('\n', testOutput));

				if (failed.Count == 0)
				{
					return new UnstickCheck(false, SummariseRun("Tests", false, testOutput));
				}

				baseline ??= await MeasureTestBaselineAsync(row, token).ConfigureAwait(false);

				var added = failed
					.Where(name => !baseline.Contains(name))
					.Order(StringComparer.Ordinal)
					.ToList();

				return added.Count == 0
					? new UnstickCheck(
						true,
						$"{failed.Count.ToString(CultureInfo.InvariantCulture)} test(s) fail, and every one failed before "
						+ "these changes too.")
					: new UnstickCheck(
						false,
						"These tests passed before the changes and fail now: "
						+ $"{string.Join(", ", added.Take(10))}.\n{SummariseRun("Tests", false, testOutput)}");
			}

			Say("🔨 Building and testing the changes as they stand...");
			var check = await JudgeAsync(cancellationToken).ConfigureAwait(false);

			var resolved = check.Passed;

			if (resolved)
			{
				Say($"✅ {check.Summary} Committing them as they are.");
			}
			else
			{
				Say($"⚠️ These changes are not safe to commit as they are: {FirstLine(check.Summary)}");

				resolved = await RepairUncommittedWithModelAsync(item, row, changed, check.Summary, JudgeAsync, progress, cancellationToken)
					.ConfigureAwait(false);
			}

			var pushed = false;

			if (resolved)
			{
				pushed = await CommitUnstuckChangesAsync(item, row, cancellationToken).ConfigureAwait(false);
			}

			if (!pushed)
			{
				await SetChangesAsideAsync(row).ConfigureAwait(false);
			}

			var synced = await SyncAfterUnstickAsync(row, cancellationToken).ConfigureAwait(false);

			item.Succeeded = synced;
		}
		catch (OperationCanceledException)
		{
			Say("⏹️ Stopped. Any changes still in the working tree are left as they are.");
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
			item.Succeeded = false;
		}
	}

	private static string FirstLine(string text)
		=> text.Split('\n', 2)[0].Trim();

	/// <summary>
	/// Which tests fail on the last commit, found by setting the uncommitted changes aside for the duration.
	/// </summary>
	/// <remarks>
	/// The changes are stashed and brought straight back in a <c>finally</c>, so a failure or a stop in the
	/// middle cannot leave them in the stash unannounced. If the stash itself fails the baseline is empty,
	/// which is the cautious answer: every failing test then counts against the changes.
	/// </remarks>
	private async Task<IReadOnlySet<string>> MeasureTestBaselineAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		Say("🧪 Some tests fail. Finding out which of them already failed before these changes...");

		var (stashed, stashOutput) = await localRepo
			.StashLocalChangesAsync(row.RepositoryFullName, "unstick: measuring which tests already fail", cancellationToken)
			.ConfigureAwait(false);

		if (!stashed)
		{
			Say($"⚠️ Could not set the changes aside to measure that ({FirstLine(stashOutput)}). Counting every failure against them.");
			return new HashSet<string>();
		}

		try
		{
			var buildOutput = new List<string>();
			await dashboard.BuildAsync(row, buildOutput.Add, cancellationToken, restore: true).ConfigureAwait(false);

			if (row.Status != PackageStatus.BuildSucceeded)
			{
				Say("ℹ️ The last commit does not build here either, so no test can be said to have passed on it.");
				return new HashSet<string>();
			}

			var testOutput = new List<string>();
			await dashboard.RunTestsAsync(row, testOutput.Add, cancellationToken, sandbox: true).ConfigureAwait(false);

			var failed = CoverageMeasurement.ParseFailedTests(string.Join('\n', testOutput));
			Say($"ℹ️ {failed.Count.ToString(CultureInfo.InvariantCulture)} test(s) already fail on the last commit.");
			return failed;
		}
		finally
		{
			var (restored, popOutput) = await localRepo
				.PopStashAsync(row.RepositoryFullName, CancellationToken.None)
				.ConfigureAwait(false);

			Say(restored
				? "↩️ The changes are back in the working tree."
				: $"⚠️ Could not bring the changes back automatically ({FirstLine(popOutput)}). They are in the newest entry of "
					+ $"`git stash list` in {row.LocalPath}.");
		}
	}

	/// <summary>
	/// Lets the local model repair the uncommitted changes, confined to the files they touch.
	/// </summary>
	/// <returns>Whether the tree now builds and passes, and so is safe to commit.</returns>
	private async Task<bool> RepairUncommittedWithModelAsync(
		WorkItem item,
		RepositoryDashboardRow row,
		IReadOnlyList<string> changed,
		string problem,
		Func<CancellationToken, Task<UnstickCheck>> judge,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		var ollama = runtimeSettings.Ollama;

		if (!ollama.IsConfigured)
		{
			Say("ℹ️ No Ollama server is configured, so nothing can repair them.");
			return false;
		}

		var (files, folders) = UnstickPrompt.PathsIn(changed);
		var succeeded = false;
		AiFixToolbox? toolbox = null;

		try
		{
			Say($"⏳ Waiting for the model ({ollama.Model})...");
			using var hold = await ollamaGate.EnterAsync(cancellationToken).ConfigureAwait(false);

			using var client = new Ollama.Api.OllamaClient(new Ollama.Api.OllamaClientOptions
			{
				Uri = new Uri(ollama.BaseUrl!),
				ApiKey = ollama.ApiKey,
				Timeout = TimeSpan.FromMilliseconds(ollama.RequestTimeoutMs)
			});

			toolbox = new AiFixToolbox(
				row.LocalPath!,
				build: async token =>
				{
					var captured = new List<string>();
					await dashboard.BuildAsync(row, captured.Add, token, restore: true).ConfigureAwait(false);
					return SummariseRun("Build", row.Status == PackageStatus.BuildSucceeded, captured);
				},
				writablePaths: files.Count + folders.Count == 0 ? null : files,
				writableFolders: folders.Count == 0 ? null : folders,

				// The oracle runs the tests, with the baseline; a bare run_tests would show the model failures it
				// did not cause, which is how a session once came to edit a library's integration tests.
				offerRunTests: false,
				restoreFile: (path, token) => localRepo.RestoreFileAsync(row.RepositoryFullName, path, token));

			var fixOptions = new AiFixOptions
			{
				MaxTurnsPerAttempt = ollama.MaxTurnsPerAttempt,
				MaxAttempts = ollama.MaxAttemptsPerRule,
				TimeBudget = _sessionTimeBudget
			};

			using var sessionLog = OpenSessionLog(row.RepositoryFullName, "unstick");

			var session = new AiFixSession(
				new OllamaChatModel(client, ollama.Model!, ollama.ContextWindow, ollama.ToDirect()),
				toolbox,
				fixOptions,
				Say,
				AiTranscriptSink.For(item.Transcript),
				sessionLog,
				onStatus: progress.Report);

			var request = new AiFixRequest(
				row.RepositoryFullName,
				"unstick",
				"Uncommitted changes",
				UnstickPrompt.BuildTask(row.RepositoryFullName, changed, problem),
				AiFixPrompt.SystemPrompt);

			LogSessionStart(sessionLog, request, ollama, fixOptions, [.. files, .. folders]);

			item.Transcript.Append(WorkLineKind.Prompt, $"System prompt:\n{request.SystemPrompt}");
			item.Transcript.Append(WorkLineKind.Prompt, $"Task:\n{request.Task}");

			Say($"▶ Repairing the uncommitted changes in {row.RepositoryFullName} via {ollama.Model}.");

			var outcome = await session
				.RunAsync(
					request,
					async token =>
					{
						var verdict = await judge(token).ConfigureAwait(false);
						Say(verdict.Passed ? $"   ✓ {verdict.Summary}" : $"   ⚠️ {FirstLine(verdict.Summary)}");
						return new AiRuleCheck(verdict.Passed, verdict.Summary);
					},
					cancellationToken)
				.ConfigureAwait(false);

			succeeded = outcome.Succeeded;

			Say(succeeded
				? "✅ The model got the changes building and passing."
				: "❌ The model could not get the changes building and passing.");

			return succeeded;
		}
		finally
		{
			if (!succeeded && toolbox is not null)
			{
				// Back to the uncommitted state this item found, not to the last commit: the stash that
				// follows takes whatever is there, and a half-repair would only make that harder to read.
				var restored = toolbox.Undo();
				if (restored > 0)
				{
					Say($"↩️ Put {restored} file(s) back as the model found them.");
				}
			}
		}
	}

	/// <summary>
	/// Commits and pushes changes that built and passed, recording the push whatever came of it.
	/// </summary>
	/// <returns>Whether they were pushed.</returns>
	private async Task<bool> CommitUnstuckChangesAsync(WorkItem item, RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		var push = await dashboard
			.CommitAndPushAsync(row, $"NuGet governance remediation for {row.RepositoryFullName}", Say, cancellationToken)
			.ConfigureAwait(false);

		if (governance is not null)
		{
			await governance
				.RecordPushAsync(item, row.RepositoryFullName, push.Success, push.RefusalReason, row.LocalPath, CancellationToken.None)
				.ConfigureAwait(false);
		}

		if (push.Success)
		{
			Say("✅ Committed and pushed.");
			return true;
		}

		Say(push.WasRefused
			? $"⛔ The push was refused: {push.RefusalReason}"
			: "❌ Commit and push failed.");

		return false;
	}

	/// <summary>
	/// Sets everything uncommitted aside in a git stash so the clone can sync, and says exactly where it went.
	/// </summary>
	private async Task SetChangesAsideAsync(RepositoryDashboardRow row)
	{
		var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

		var (stashed, output) = await localRepo
			.StashLocalChangesAsync(
				row.RepositoryFullName,
				$"unstick {stamp} UTC: uncommitted changes that could not be made to build and pass",
				CancellationToken.None)
			.ConfigureAwait(false);

		Say(stashed
			? "📦 Set the changes aside in a git stash — nothing was thrown away. To look at them: "
				+ $"`git stash list` and `git stash show -p` in {row.LocalPath}; to bring them back: `git stash pop`."
			: $"⚠️ Could not set the changes aside: {FirstLine(output)}");

		await dashboard.RefreshGitStatusAsync(row, CancellationToken.None).ConfigureAwait(false);
	}

	/// <summary>Syncs the clone with its remote once it is clean, and reports where it stands.</summary>
	private async Task<bool> SyncAfterUnstickAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		var synced = await dashboard.GitSyncAsync(row, Say, cancellationToken).ConfigureAwait(false);

		Say(synced ? "✅ Git sync complete." : "❌ Git sync failed.");
		cache.UpsertRow(row);

		if (row.CurrentBranch is not null)
		{
			Say($"ℹ️ Branch: {row.CurrentBranch} | Clean: {row.IsWorkingTreeClean} | Synced: {row.IsSyncedWithOrigin}");
		}

		return synced;
	}

	/// <summary>
	/// Pulls and pushes one repository, then reports where its working tree stands.
	/// </summary>
	/// <param name="item">The item naming the repository to sync.</param>
	/// <param name="progress">Unused: a sync reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task GitSyncAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		Say("▶ Git syncing...");

		try
		{
			var synced = await dashboard.GitSyncAsync(row, Say, cancellationToken).ConfigureAwait(false);

			Say(synced ? "✅ Git sync complete" : "❌ Git sync failed");
			cache.UpsertRow(row);

			if (row.CurrentBranch is not null)
			{
				Say($"ℹ️ Branch: {row.CurrentBranch} | Clean: {row.IsWorkingTreeClean} | Synced: {row.IsSyncedWithOrigin}");
			}

			await SayDirtyWorkingTreePreviewAsync(row).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Stopping is not failing, and every other step says so the same way. Without this rethrow
			// a stopped sync reports "❌ Error: The operation was canceled" as though something had gone
			// wrong, instead of the runner's stop line.
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Publishes one repository's packages.
	/// </summary>
	/// <param name="item">The item naming the repository to publish.</param>
	/// <param name="progress">Unused: a publish reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task PublishAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		Say("▶ Publishing...");

		try
		{
			await dashboard.RunPublishAsync(row, Say, cancellationToken).ConfigureAwait(false);

			Say(row.Status == PackageStatus.Published ? "✅ Published" : "❌ Publish failed");

			governance?.RecordPublish(
				item,
				row.RepositoryFullName,
				row.Status == PackageStatus.Published,
				[.. row.Packages.Select(package => package.PackageId)],
				row.LatestTag,
				row.NextVersion,
				row.TotalFailures);
		}
		catch (OperationCanceledException)
		{
			// Stopping is not failing, and every other step says so the same way. Without this rethrow
			// a stopped publish reports "❌ Error: The operation was canceled" as though something had
			// gone wrong, instead of the runner's stop line.
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Commits and pushes one repository's working tree.
	/// </summary>
	/// <remarks>
	/// A guard's refusal used to be raised as a dialog, on the grounds that a console can be
	/// collapsed. There is no dialog to raise from a lane — and twenty lanes would raise twenty — so
	/// it is logged as a warning instead, which is the loudest thing the console has.
	/// </remarks>
	/// <param name="item">The item naming the repository to commit and push.</param>
	/// <param name="progress">Unused: a commit and push reports no sub-steps.</param>
	/// <param name="cancellationToken">Unused: the underlying git calls are not interruptible.</param>
	private async Task CommitAndPushAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		Say("▶ Committing and pushing...");

		try
		{
			var commitMessage = $"NuGet governance remediation for {row.RepositoryFullName}";
			var push = await dashboard.CommitAndPushAsync(row, commitMessage, Say).ConfigureAwait(false);

			// Whatever the outcome: a refused or failed push is as much part of the record as a good one.
			if (governance is not null)
			{
				await governance.RecordPushAsync(item, row.RepositoryFullName, push.Success, push.RefusalReason, row.LocalPath, CancellationToken.None).ConfigureAwait(false);
			}

			if (push.Success)
			{
				Say("✅ Changes committed and pushed");
				cache.UpsertRow(row);
				item.Succeeded = true;

				if (row.CurrentBranch is not null)
				{
					Say($"ℹ️ Branch: {row.CurrentBranch} | Clean: {row.IsWorkingTreeClean} | Synced: {row.IsSyncedWithOrigin}");
				}
			}
			else
			{
				Say("❌ Commit and push failed");
				cache.UpsertRow(row);
				item.Succeeded = false;

				if (push.WasRefused)
				{
					logger.LogWarning(
						"Nothing was pushed for {Repository}: {Reason}",
						row.RepositoryFullName,
						push.RefusalReason);
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Stopping is not failing. Rethrown so the runner marks the item Cancelled and logs
			// the stop, and so the step badge is left alone rather than painted red for work the
			// user chose to end.
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
			item.Succeeded = false;
		}
	}

	/// <summary>
	/// Names the first few uncommitted files when a working tree is dirty, and says nothing at all
	/// when it is clean.
	/// </summary>
	/// <remarks>
	/// A dirty tree is what stops a fix being pushed, so the reason is put in front of the user at
	/// the moment it is discovered rather than left for them to go and find with git.
	/// </remarks>
	/// <param name="row">The repository whose working tree to describe.</param>
	private async Task SayDirtyWorkingTreePreviewAsync(RepositoryDashboardRow row)
	{
		if (row.IsWorkingTreeClean != false)
		{
			return;
		}

		var lines = await dashboard.GetWorkingTreeStatusPreviewAsync(row, maxLines: 3).ConfigureAwait(false);
		if (lines.Count == 0)
		{
			Say("ℹ️ Working tree is dirty but no git porcelain entries were captured.");
			return;
		}

		Say("ℹ️ Dirty files (git status --porcelain):");
		foreach (var line in lines)
		{
			Say($"  {line}");
		}
	}

	/// <summary>
	/// A GitHub client carrying the signed-in user's token when one can be read.
	/// </summary>
	/// <remarks>
	/// The token belongs to a sign-in, and work no longer belongs to the browser tab that started
	/// it: by the time a lane reaches an item there is no request in flight at all. The runner's
	/// asynchronous flow starts at the host rather than at a request, so
	/// <c>IHttpContextAccessor.HttpContext</c> is null here always, not merely sometimes — which is
	/// why the token is taken from <see cref="GitHubTokenProvider"/>, where a circuit published it
	/// while it still had a request to read it from. The accessor is kept only as a fallback for a
	/// caller that does happen to be on a request thread.
	/// <para>
	/// With neither, the client is anonymous: it cannot see private repositories at all, and shares
	/// the 60-per-hour anonymous rate limit — which one fanned-out organisation re-assessment
	/// exhausts on its first pass. That is worth a warning rather than being passed off as normal.
	/// </para>
	/// </remarks>
	private async Task<IGitHubClient> CreateGitHubClientAsync()
	{
		var client = new GitHubClient(new ProductHeaderValue("PanoramicData.NugetManagement.Web"));

		var accessToken = gitHubTokens.AccessToken;

		if (string.IsNullOrWhiteSpace(accessToken) && httpContextAccessor.HttpContext is { } httpContext)
		{
			accessToken = await httpContext.GetTokenAsync("access_token").ConfigureAwait(false);
		}

		if (string.IsNullOrWhiteSpace(accessToken))
		{
			logger.LogWarning(
				"⚠️ No signed-in GitHub token is available, so this assessment will run anonymously: "
				+ "private repositories cannot be read at all, and public ones share the 60-requests-per-hour "
				+ "anonymous rate limit. Sign in and open the dashboard once to give the work runner a token.");
			return client;
		}

		client.Credentials = new Credentials(accessToken);
		return client;
	}
}
