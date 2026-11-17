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

public sealed partial class WorkExecutors
{
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
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task BuildAsync(WorkItem item, CancellationToken cancellationToken)
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
				cancellationToken, false).ConfigureAwait(false);

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
		// Stopping is not failing: cancellation propagates so the item is marked Cancelled.
		catch (Exception ex) when (ex is not OperationCanceledException)
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
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task CloneAsync(WorkItem item, CancellationToken cancellationToken)
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
		catch (Exception ex) when (ex is not OperationCanceledException)
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
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private Task DiscoverCloneTargetsAsync(WorkItem item, CancellationToken cancellationToken)
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
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task RediscoverOrganizationAsync(WorkItem item, CancellationToken cancellationToken)
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
	/// <param name="cancellationToken">Unused: discovery here is a synchronous cache read.</param>
	private Task DiscoverReassessTargetsAsync(WorkItem item, CancellationToken cancellationToken)
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
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task RefreshAllAsync(WorkItem item, CancellationToken cancellationToken)
	{
		Say("▶ Discovering packages...");

		try
		{
			var freshRows = await dashboard.DiscoverPackagesAsync(
				null,
				cancellationToken,
				await CreateGitHubClientAsync().ConfigureAwait(false)).ConfigureAwait(false);
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
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task ReassessAsync(WorkItem item, CancellationToken cancellationToken)
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

			if (await RemoveIfRetiredAsync(row).ConfigureAwait(false))
			{
				return;
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
				await dashboard.AssessRepositoryAsync(row, github, CancellationToken.None).ConfigureAwait(false);
			}

			cache.UpsertRow(row);
			Say($"✅ Assessment complete for {row.RepositoryFullName}");
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Say($"❌ Assessment failed: {ex.Message}");
		}
	}

	/// <summary>
	/// Checks which of a repository's packages are still listed on NuGet, and drops the repository
	/// from the cache when none are.
	/// </summary>
	/// <param name="row">The repository being re-assessed.</param>
	/// <returns>True when the repository was removed and there is nothing left to assess.</returns>
	private async Task<bool> RemoveIfRetiredAsync(RepositoryDashboardRow row)
	{
		var listed = new List<string>();
		foreach (var package in row.Packages)
		{
			if (await dashboard.IsPackageListedAsync(package.PackageId, CancellationToken.None).ConfigureAwait(false))
			{
				listed.Add(package.PackageId);
			}
		}

		if (HasRetiredEveryPackage(row.Packages.Count, listed.Count))
		{
			Say($"⚠️ {row.RepositoryFullName} publishes nothing still listed on NuGet — removing from cache.");
			cache.RemoveRow(row.RepositoryFullName);
			return true;
		}

		if (listed.Count < row.Packages.Count)
		{
			var retired = row.Packages
				.Select(package => package.PackageId)
				.Except(listed, StringComparer.OrdinalIgnoreCase);

			Say($"ℹ️ {row.RepositoryFullName}: {string.Join(", ", retired)} no longer listed on NuGet.");
		}

		return false;
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
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task FixAllAsync(WorkItem item, CancellationToken cancellationToken)
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

			await ReassessAfterFixAsync(row, cancellationToken).ConfigureAwait(false);

			ReportIssuesWithoutAutoFix(item, row);
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

	/// <summary>Reports the issues that have no auto-fix and leaves an AI prompt for them on the item.</summary>
	private void ReportIssuesWithoutAutoFix(WorkItem item, RepositoryDashboardRow row)
	{
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

	private async Task ReassessAfterFixAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		Say("▶ Re-assessing...");

		// The client is what lets the local assessment read the repository's inbox, so a
		// re-assessment after a fix reports the same issue state a fresh one would.
		await dashboard.AssessLocalRepositoryAsync(
			row,
			cancellationToken,
			await CreateGitHubClientAsync().ConfigureAwait(false)).ConfigureAwait(false);
		cache.UpsertRow(row);
	}

	/// <summary>
	/// Applies the auto-remediations of one assessment category to a repository, then re-assesses it.
	/// </summary>
	/// <param name="item">The item naming the repository and, via <c>category</c>, which category to fix.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task FixCategoryAsync(WorkItem item, CancellationToken cancellationToken)
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

			await ReassessAfterFixAsync(row, cancellationToken).ConfigureAwait(false);
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
}
