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
	/// Builds a local repository.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="onOutput">Where the build's output goes.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <param name="restore">
	/// Whether to restore packages first. Off by default, for speed. A caller that has just changed what
	/// is referenced — a model swapping one package for another — has to turn it on, because a build that
	/// skips restore compiles against the old references, passes, and is then followed by a test run
	/// against assemblies that never matched the project files.
	/// </param>
	public async Task BuildAsync(
		RepositoryDashboardRow row,
		Action<string>? onOutput,
		CancellationToken cancellationToken,
		bool restore)
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

		var (success, _) = await _localRepo.BuildAsync(repoIdentity, onOutput, cancellationToken, restore).ConfigureAwait(false);

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
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var repoIdentity = RepoIdentity(row);
		if (repoIdentity is null)
		{
			row.Status = PackageStatus.Error;
			row.StatusMessage = "Cannot determine repo name.";
			return false;
		}

		if (!await EnsureClonedAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false))
		{
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

	private async Task<bool> EnsureClonedAsync(RepositoryDashboardRow row, string repoIdentity, Action<string>? onOutput, CancellationToken cancellationToken)
	{
		if (!_localRepo.IsClonedLocally(repoIdentity))
		{
			return await CloneAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);
		}

		if (await VerifyLocalCloneAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false) is null)
		{
			return true;
		}

		// An existing folder of the right name holding the wrong repository: syncing it would pull
		// somebody else's history into what this row believes is its checkout.
		row.Status = PackageStatus.Error;
		row.StatusMessage = "Local folder holds a different repository.";
		return false;
	}

	private async Task<bool> CloneAsync(RepositoryDashboardRow row, string repoIdentity, Action<string>? onOutput, CancellationToken cancellationToken)
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
		return true;
	}

	/// <summary>
	/// Commits all local changes, fetches, rebases on remote, and pushes.
	/// Does not change the row status (preserves current workflow state).
	/// </summary>
	/// <remarks>
	/// Refuses, and reverts the uncommitted changes, when the clone does not build with them. The token
	/// stops the checks and the build only; the push itself is never cancelled.
	/// </remarks>
	public async Task<CommitAndPushOutcome> CommitAndPushAsync(
		RepositoryDashboardRow row,
		string commitMessage,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
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

		var buildRefusal = await VerifyChangesBuildAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);
		if (buildRefusal is not null)
		{
			return buildRefusal;
		}

		// Not cancellable: a push killed mid-ref-update cannot be tidied up afterwards.
		var (success, _) = await _localRepo.CommitAndPushAsync(repoIdentity, commitMessage, onOutput, CancellationToken.None).ConfigureAwait(false);

		if (success)
		{
			// Verified again in the background so a breakage the local build could not see is rolled back.
			_regressionGuard.Enqueue(row.RepositoryFullName);

			// Refresh git status after push. The push itself establishes that we match origin, so no
			// fetch is needed to know it — but it is knowledge with an age, like any other.
			row.CurrentBranch = await _localRepo.GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
			row.IsWorkingTreeClean = await _localRepo.IsWorkingTreeCleanAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
			// Same reasoning: what was here has just gone to origin, so there is nothing left ahead of it.
			row.HasUnpushedCommits = false;
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
	/// Builds the clone before anything in it is committed or pushed, and refuses when it does not build.
	/// Returns null when the push may go ahead.
	/// </summary>
	/// <remarks>
	/// #196: a BLD-02 remediation committed to Rundeck.Cli's main without building and left it red for days.
	/// Uncommitted changes that break the build are stashed, so the clone is restored and the rule stays
	/// failing; unpushed commits are left alone and only refused.
	/// </remarks>
	private async Task<CommitAndPushOutcome?> VerifyChangesBuildAsync(
		RepositoryDashboardRow row,
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var isClean = await _localRepo.IsWorkingTreeCleanAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		var isAhead = await _localRepo.HasUnpushedCommitsAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		if (isClean == true && isAhead == false)
		{
			return null;
		}

		onOutput?.Invoke($"🔎 Verifying {row.RepositoryFullName} builds before committing...");
		var build = await PushBuildGate.BuildAsync(() => _localRepo.BuildWithRestoreAsync(repoIdentity, onOutput, cancellationToken)).ConfigureAwait(false);
		if (build.Success)
		{
			return null;
		}

		var changed = await _localRepo.GetWorkingTreeStatusPreviewAsync(repoIdentity, int.MaxValue, cancellationToken).ConfigureAwait(false);
		var reason = $"Nothing was committed or pushed: {row.RepositoryFullName} does not build with these changes.";

		if (changed.Count == 0)
		{
			return CommitAndPushOutcome.Refused(Refuse(reason + " Its unpushed commits were left in place for a person to look at.", onOutput));
		}

		var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
		var (stashed, stashOutput) = await _localRepo
			.StashLocalChangesAsync(repoIdentity, $"build gate {stamp} UTC: changes that did not build", CancellationToken.None)
			.ConfigureAwait(false);

		reason += $"\nChanged: {string.Join(", ", changed.Select(line => line.Trim()))}";
		if (!stashed)
		{
			return CommitAndPushOutcome.Refused(Refuse($"{reason}\nThe changes could not be set aside ({stashOutput.Trim()}), so clear the clone by hand.", onOutput));
		}

		row.IsWorkingTreeClean = true;
		reason += $"\nThey were reverted and kept in `git stash list` in {_localRepo.GetLocalPath(repoIdentity)}.";
		return CommitAndPushOutcome.Refused(Refuse(reason, onOutput)) with { ChangesReverted = true };
	}

	/// <summary>
	/// The single gate every write to a local clone passes through: it is the repository we think it is,
	/// it has nothing uncommitted in it, and it is not behind origin. Returns false — having said why — if
	/// any of those does not hold.
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
			&& await VerifyCleanWorkingTreeAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false)
			&& await CatchUpWithOriginAsync(row, repoIdentity, onOutput, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Fast-forwards a clean clone that is behind origin, so a fix is applied to the tree it will be
	/// pushed onto. Returns false, having said why, when the clone has diverged and cannot be.
	/// </summary>
	/// <remarks>
	/// Without this a fix applied to a stale clone could never be pushed: <see cref="VerifyNotBehindOriginAsync"/>
	/// refuses the push and discards the fix, and applying it again repeats the same cycle, because
	/// nothing in between brings the clone up to date. When origin cannot be reached the fix goes ahead
	/// against what the clone has — it is local and undoable — and the push gate fails closed as before.
	/// </remarks>
	private async Task<bool> CatchUpWithOriginAsync(
		RepositoryDashboardRow row,
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var (caughtUp, behind) = await _localRepo.FastForwardToOriginAsync(repoIdentity, cancellationToken).ConfigureAwait(false);

		if (behind is null or 0)
		{
			return true;
		}

		if (!caughtUp)
		{
			onOutput?.Invoke(
				$"❌ {row.RepositoryFullName}: origin has {behind} commit(s) this clone does not, and the clone has commits of its own, so it cannot simply be brought up to date. Sync it, then apply the fix again.");
			return false;
		}

		onOutput?.Invoke($"↻ {row.RepositoryFullName}: brought the clone up to date with origin ({behind} new commit(s)) before applying.");
		await RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
		return true;
	}

	// ── Issue-centric ("dimensional flip") view + bulk apply across repositories ──
}
