using Microsoft.AspNetCore.Authentication;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

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
public sealed class WorkExecutors(
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
	AiPlaybookRegistry playbooks)
{
	/// <summary>Every kind this service knows how to run.</summary>
	/// <remarks>
	/// Exposed so a test can assert it covers <see cref="WorkKind"/> in full. A kind that can be
	/// queued but not run would sit in a lane for ever, blocking everything behind it.
	/// </remarks>
	public static IReadOnlySet<WorkKind> SupportedKinds { get; } = new HashSet<WorkKind>
	{
		WorkKind.Clone, WorkKind.Reassess, WorkKind.FixAll, WorkKind.FixCategory, WorkKind.FixRule,
		WorkKind.FixWithAiRule,
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
	/// Throws away the repository's remembered build result when the work that just ran could have
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
		if (row is null || row.LastBuildState is null)
		{
			return;
		}

		row.LastBuildState = null;
		row.LastBuiltAtUtc = null;
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

	/// <summary>
	/// Says something in the console the item was queued from.
	/// </summary>
	/// <remarks>
	/// Logged rather than written into a buffer: the work has no console of its own, and the UI
	/// console subscribes to this category. The line is passed as an argument rather than as the
	/// template so braces in a path or a git message are not read as structured-logging placeholders.
	/// </remarks>
	/// <param name="line">The line to say.</param>
	private void Say(string line) => logger.LogInformation("{ConsoleLine}", line);

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

			var freshRows = await dashboard.DiscoverPackagesAsync(organization, cancellationToken).ConfigureAwait(false);
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
			var freshRows = await dashboard.DiscoverPackagesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
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
	/// Re-checks one repository's packages against the NuGet listing and, if any are still listed,
	/// re-assesses it against every rule.
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

			if (listed.Count == 0)
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

			var toolbox = new AiFixToolbox(
				row.LocalPath,
				build: async token =>
				{
					await dashboard.BuildAsync(row, Say, token).ConfigureAwait(false);
					return row.StatusMessage;
				},
				test: async token =>
				{
					await dashboard.RunTestsAsync(row, Say, token).ConfigureAwait(false);
					return row.StatusMessage;
				});

			var playbook = playbooks.For(ruleId);

			Say($"▶ {ruleId} on {row.RepositoryFullName} via {ollama.Model}"
				+ $"{(playbook is null ? " (no playbook — using the advisory)" : string.Empty)}.");

			var session = new AiFixSession(
				new OllamaChatModel(client, ollama.Model!, ollama.ContextWindow),
				toolbox,
				new AiFixOptions
				{
					MaxTurnsPerAttempt = ollama.MaxTurnsPerAttempt,
					MaxAttempts = ollama.MaxAttemptsPerRule
				},
				Say);

			var request = new AiFixRequest(
				row.RepositoryFullName,
				ruleId,
				result.RuleName,
				AiFixPrompt.BuildTask(result, row.RepositoryFullName, playbook),
				AiFixPrompt.SystemPrompt);

			var outcome = await session
				.RunAsync(request, token => ReEvaluateAsync(row, ruleId, token), cancellationToken)
				.ConfigureAwait(false);

			succeeded = outcome.Succeeded;

			if (succeeded)
			{
				// The one rule is re-evaluated rather than the whole repository: it is the only thing that
				// can have changed, and a full re-assessment here would cost far more than the fix did.
				var after = await ReEvaluateAsync(row, ruleId, cancellationToken).ConfigureAwait(false);
				ReplaceRuleResult(row, ruleId, after);

				await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
				cache.UpsertRow(row);

				Say($"✅ {outcome.Summary} Review the working tree before committing — a model wrote it.");
			}
		}
		catch (OperationCanceledException)
		{
			Say($"⏹️ Stopped {ruleId} on {row.RepositoryFullName}.");
			throw;
		}
		finally
		{
			if (!succeeded)
			{
				await RevertPartAppliedFixAsync(row).ConfigureAwait(false);
			}
		}
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
			var applied = new List<string>();
			dashboard.ApplySingleRemediationPublic(row.LocalPath, result, applied, Say);
			remediationCompleted = true;

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

		var outcome = await triageRunner
			.RunAsync(
				new OctokitGitHubIssueApi(github),
				new OctokitGitHubWriteApi(github),
				row.RepositoryFullName,
				triages,
				Say,
				cancellationToken)
			.ConfigureAwait(false);

		Say($"✅ {row.RepositoryFullName}: closed {outcome.Closed}, "
			+ $"{outcome.Covered} awaiting an existing fix, {outcome.Uncovered} with no fix available, "
			+ $"{outcome.Unrecognised} left alone.");

		// The closed ones have left the open list, and the survivors now carry their verdicts.
		row.OpenIssues = [.. DependabotTriageRunner.Restamp(row.OpenIssues, triages)];

		cache.UpsertRow(row);
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

			if (row.Status == PackageStatus.TestsPassed)
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
