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
	/// Applies automatic file-based remediations for all failed rules that have
	/// a registered remediation.
	/// Returns the list of files created/modified.
	/// </summary>
	public async Task<List<string>> ApplyRemediationsAsync(
		RepositoryDashboardRow row,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var applied = new List<string>();

		if (!await IsReadyForRemediationAsync(row, onOutput, cancellationToken).ConfigureAwait(false))
		{
			return applied;
		}

		ApplyFailures(row, applied, onOutput, cancellationToken);

		await ApplyFollowOnPassesAsync(row, applied, onOutput, cancellationToken).ConfigureAwait(false);

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

	private const int MaxFollowOnPasses = 3;

	private async Task<bool> IsReadyForRemediationAsync(RepositoryDashboardRow row, Action<string>? onOutput, CancellationToken cancellationToken)
	{
		if (row.Assessment is null || row.LocalPath is null)
		{
			onOutput?.Invoke("⚠️ No assessment data or local path — cannot apply remediations.");
			return false;
		}

		return await VerifyWritableCloneAsync(row, onOutput, cancellationToken).ConfigureAwait(false);
	}

	private async Task ApplyFollowOnPassesAsync(
		RepositoryDashboardRow row,
		List<string> applied,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		// A fix can change what else fails: moving a project to net11.0 makes the packages that framework
		// supplies fail PKG-15. Those could only be applied by a second run, which the clean-tree gate
		// refuses because the first run's edits are uncommitted. Follow on inside this one gate instead.
		for (var pass = 1; pass <= MaxFollowOnPasses && applied.Count > 0; pass++)
		{
			var before = applied.Count;
			onOutput?.Invoke("▶ Re-assessing the working tree for fixes the last change unlocked...");
			await AssessLocalRepositoryAsync(row, cancellationToken, null).ConfigureAwait(false);
			ApplyFailures(row, applied, onOutput, cancellationToken);

			if (applied.Count == before)
			{
				break;
			}
		}
	}

	private void ApplyFailures(
		RepositoryDashboardRow row,
		List<string> applied,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var failures = row.Assessment?.RuleResults.Where(r => !r.Passed && r.Advisory is not null).ToList() ?? [];

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
			ApplySingleRemediation(row, failure, applied, onOutput);
		}
	}

	/// <summary>
	/// Applies automatic remediations for a specific category.
	/// </summary>
	public async Task<List<string>> ApplyCategoryRemediationsAsync(
		RepositoryDashboardRow row,
		AssessmentCategory category,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var applied = new List<string>();

		if (!await IsReadyForRemediationAsync(row, onOutput, cancellationToken).ConfigureAwait(false))
		{
			return applied;
		}

		var failures = row.Assessment!.RuleResults
			.Where(r => !r.Passed && r.Category == category && r.Advisory is not null)
			.ToList();

		foreach (var failure in failures)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ApplySingleRemediation(row, failure, applied, onOutput);
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
		Action<string>? onOutput,
		CancellationToken cancellationToken)
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

		ApplySingleRemediation(row, failure, applied, onOutput);
		return applied;
	}

	/// <summary>
	/// Applies a single remediation via the registry.
	/// </summary>
	private void ApplySingleRemediation(
		RepositoryDashboardRow row,
		RuleResult failure,
		List<string> applied,
		Action<string>? onOutput)
	{
		var remediation = RemediationRegistry.Get(failure.RuleId);
		if (remediation is null || !remediation.CanRemediate(failure) || IsStaleLicenseAdvisory(row, failure, onOutput))
		{
			return;
		}

		try
		{
			remediation.Apply(row.LocalPath!, failure, applied, onOutput);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed remediation for rule {RuleId}", failure.RuleId);
			onOutput?.Invoke($"❌ [{failure.RuleId}] Failed: {ex.Message}");
		}
	}

	/// <summary>
	/// Whether a licence rule's advisory was written for a licence other than the one the repository is
	/// now held to, so applying it would write the old licence back.
	/// </summary>
	private bool IsStaleLicenseAdvisory(RepositoryDashboardRow row, RuleResult failure, Action<string>? onOutput)
	{
		if (failure.Advisory is null || row.RepositoryFullName is null)
		{
			return false;
		}

		var current = _runtimeSettings.ResolveLicense(row.RepositoryFullName).License;
		if (StaleLicenseAdvisoryLicense(failure, current) is not { } stated)
		{
			return false;
		}

		onOutput?.Invoke($"⚠️ [{failure.RuleId}] Skipped: assessed against licence {stated}, but {row.RepositoryFullName} is now held to {current}. Re-assess first.");
		return true;
	}

	/// <summary>
	/// The licence a stale licence advisory was assessed against, or null when it is current or names none.
	/// </summary>
	internal static string? StaleLicenseAdvisoryLicense(RuleResult failure, string currentLicense)
	{
		var stated = failure.Advisory is null
			? []
			: DataDrivenRemediation.ReadStrings(failure.Advisory.Data, "expected_license", "expected_license_type");

		// COM-02 advisories cached before it named a licence would replace a kept licence note.
		if (stated.Length == 0)
		{
			return string.Equals(failure.RuleId, "COM-02", StringComparison.OrdinalIgnoreCase) ? "unknown" : null;
		}

		return string.Equals(stated[0], currentLicense, StringComparison.OrdinalIgnoreCase) ? null : stated[0];
	}

	private Task<List<string>> ApplySingleRuleAsync(RepositoryDashboardRow row, string ruleId, Action<string>? onOutput)
	{
		var applied = new List<string>();
		var fresh = row.Assessment?.RuleResults
			.FirstOrDefault(rr => !rr.Passed && string.Equals(rr.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
		if (fresh is not null && row.LocalPath is not null && IsAutoRemediable(fresh))
		{
			ApplySingleRemediation(row, fresh, applied, onOutput);
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
		var run = new BulkApplyRun(applyFunc, commitMessage, onOutput, onRepositoryFixed);
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
			run.Phase = RepoApplyPhase.NotStarted;

			try
			{
				if (!await ApplyToRepositoryAsync(row, run, cancellationToken).ConfigureAwait(false))
				{
					break;
				}
			}
			catch (OperationCanceledException)
			{
				await RollBackAsync(row, run, onOutput).ConfigureAwait(false);
				throw;
			}
			catch (Exception ex)
			{
				StopWithFailure(run, name, ex, onOutput);
				break;
			}
		}

		return run.Outcome;
	}

	private async Task RollBackAsync(RepositoryDashboardRow row, BulkApplyRun run, Action<string>? onOutput)
	{
		// Atomic per repository: work that never reached its commit is undone, so the clone is
		// left as it was found rather than half-remediated.
		if (BulkApplyCancellation.NeedsRevert(run.Phase))
		{
			await RevertUncommittedAsync(row, row.RepositoryFullName, onOutput).ConfigureAwait(false);
		}

		run.Outcome.Results.Add(BulkApplyCancellation.Describe(row.RepositoryFullName, run.Phase));
	}

	private static void StopWithFailure(BulkApplyRun run, string name, Exception ex, Action<string>? onOutput)
	{
		run.Outcome.Results.Add(new RepoApplyResult
		{
			RepositoryFullName = name,
			Status = RepoApplyStatus.Failed,
			Message = ex.Message
		});
		run.Outcome.StoppedOnFailure = true;
		onOutput?.Invoke($"⛔ Stopping bulk apply: {ex.Message}");
	}

	/// <summary>Applies the change to one repository, returning false when the whole run must stop.</summary>
	private async Task<bool> ApplyToRepositoryAsync(RepositoryDashboardRow row, BulkApplyRun run, CancellationToken cancellationToken)
	{
		var name = row.RepositoryFullName;
		if (!await IsReadyToApplyAsync(row, run, cancellationToken).ConfigureAwait(false))
		{
			return true;
		}

		await AssessLocalRepositoryAsync(row, cancellationToken, null).ConfigureAwait(false);

		run.Phase = RepoApplyPhase.Applying;
		var applied = await run.ApplyFunc(row).ConfigureAwait(false);
		run.Phase = applied.Count == 0 ? RepoApplyPhase.NotStarted : RepoApplyPhase.Applied;
		if (applied.Count == 0)
		{
			run.Outcome.Results.Add(new RepoApplyResult
			{
				RepositoryFullName = name,
				Status = RepoApplyStatus.NothingToDo,
				Message = "No changes to apply after sync."
			});
			return true;
		}

		// CommitAndPushAsync builds first. Not cancellable: the stop is honoured at the next repository.
		var push = await CommitAndPushAsync(row, run.CommitMessage, run.OnOutput, CancellationToken.None).ConfigureAwait(false);
		run.Phase = push.Success ? RepoApplyPhase.Pushed : RepoApplyPhase.Applied;
		run.Outcome.Results.Add(DescribePush(name, push, applied.Count));

		if (push.Success)
		{
			await AfterPushAsync(row, run, cancellationToken).ConfigureAwait(false);
		}

		if (push.ChangesReverted)
		{
			// The build gate left nothing behind, so the next repository is still safe to try.
			run.Phase = RepoApplyPhase.NotStarted;
			return true;
		}

		return push.Success || StopOnFailure(run, push);
	}

	/// <summary>
	/// Syncs the clone and confirms it is safe to write to, recording a skip and returning false when
	/// it is not.
	/// </summary>
	private async Task<bool> IsReadyToApplyAsync(RepositoryDashboardRow row, BulkApplyRun run, CancellationToken cancellationToken)
	{
		var name = row.RepositoryFullName;

		// Checked before the sync rather than after it: a rebase onto an already-dirty tree is
		// its own hazard, and skipping the repository entirely is the safe outcome either way.
		if (row.IsClonedLocally && !await VerifyWritableCloneAsync(row, run.OnOutput, cancellationToken).ConfigureAwait(false))
		{
			return run.Skip(name, "Skipped: the local clone is not in a state it is safe to write to.");
		}

		// Guardrail: never commit onto a stale clone — sync, then reassess the fresh tree. The
		// sync is also what clones a repository being remediated for the first time, so its
		// failure has to stop the repository here: carrying on would apply the remediation to
		// whatever the path happens to be, up to and including a directory that is not a clone.
		if (!await GitSyncAsync(row, run.OnOutput, cancellationToken).ConfigureAwait(false))
		{
			return run.Skip(name, "Skipped: the local clone could not be synced with origin.");
		}

		// The gate again, now unconditionally: the check above is skipped for a row with no
		// clone, and the sync was supposed to produce one. Remediations create the directories
		// they write into, so without this a bare directory gets filled with files belonging to
		// no repository, and nothing notices until the push refuses to touch it.
		return await VerifyWritableCloneAsync(row, run.OnOutput, cancellationToken).ConfigureAwait(false)
			|| run.Skip(name, "Skipped: the local clone is not in a state it is safe to write to.");
	}

	private static RepoApplyResult DescribePush(string name, CommitAndPushOutcome push, int fileCount)
		=> new()
		{
			RepositoryFullName = name,
			// A refusal is a skip, not a failure: a guard decided, and nothing was left behind.
			Status = push.Success
				? RepoApplyStatus.Pushed
				: push.WasRefused ? RepoApplyStatus.Skipped : RepoApplyStatus.Failed,
			Message = push.Success
				? $"{fileCount} file(s) committed & pushed."
				: push.RefusalReason ?? "Commit/push failed."
		};

	private async Task AfterPushAsync(RepositoryDashboardRow row, BulkApplyRun run, CancellationToken cancellationToken)
	{
		// Re-assessed now the fix has landed, so the row stops reporting an issue that no
		// longer exists. Without this the issue tree still shows the failures a run has just
		// fixed, because everything downstream reads the assessment taken before the change.
		await AssessLocalRepositoryAsync(row, cancellationToken, null).ConfigureAwait(false);
		run.OnRepositoryFixed?.Invoke(row);

		// CommitAndPushAsync has queued the background verification, which reverts if our change broke it.
		run.OnOutput?.Invoke($"🛡️ Queued {row.RepositoryFullName} for build verification.");
	}

	private static bool StopOnFailure(BulkApplyRun run, CommitAndPushOutcome push)
	{
		run.Outcome.StoppedOnFailure = true;
		run.OnOutput?.Invoke($"⛔ Stopping bulk apply: {(push.WasRefused ? "a guard refused the push" : "commit/push failed")}.");
		return false;
	}

	/// <summary>The state of one bulk apply: what to run, where to report, and how far the current repository got.</summary>
	private sealed class BulkApplyRun(
		Func<RepositoryDashboardRow, Task<List<string>>> applyFunc,
		string commitMessage,
		Action<string>? onOutput,
		Action<RepositoryDashboardRow>? onRepositoryFixed)
	{
		public Func<RepositoryDashboardRow, Task<List<string>>> ApplyFunc { get; } = applyFunc;

		public string CommitMessage { get; } = commitMessage;

		public Action<string>? OnOutput { get; } = onOutput;

		public Action<RepositoryDashboardRow>? OnRepositoryFixed { get; } = onRepositoryFixed;

		public BulkApplyOutcome Outcome { get; } = new();

		public RepoApplyPhase Phase { get; set; }

		public bool Skip(string name, string message)
		{
			Outcome.Results.Add(new RepoApplyResult
			{
				RepositoryFullName = name,
				Status = RepoApplyStatus.Skipped,
				Message = message
			});
			return false;
		}
	}

	private static bool RepoHasFailingRule(RepositoryDashboardRow row, string ruleId)
		=> row.Assessment?.RuleResults.Any(rr => !rr.Passed && string.Equals(rr.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)) == true;

	private static bool RepoHasFailingCategory(RepositoryDashboardRow row, AssessmentCategory category)
		=> row.Assessment?.RuleResults.Any(rr => !rr.Passed && rr.Category == category) == true;
}
