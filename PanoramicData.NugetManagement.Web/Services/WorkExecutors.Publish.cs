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
	/// Pulls and pushes one repository, then reports where its working tree stands.
	/// </summary>
	/// <param name="item">The item naming the repository to sync.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task GitSyncAsync(WorkItem item, CancellationToken cancellationToken)
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

	private void ReportPushed(WorkItem item, RepositoryDashboardRow row)
	{
		Say("✅ Changes committed and pushed");
		cache.UpsertRow(row);
		item.Succeeded = true;

		if (row.CurrentBranch is not null)
		{
			Say($"ℹ️ Branch: {row.CurrentBranch} | Clean: {row.IsWorkingTreeClean} | Synced: {row.IsSyncedWithOrigin}");
		}
	}

	private async Task HandleFailedPushAsync(WorkItem item, RepositoryDashboardRow row, CommitAndPushOutcome push, CancellationToken cancellationToken)
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

		if (push.ChangesReverted)
		{
			// The fix's re-assessment showed the rule passing; it is failing again now the fix is gone.
			await RefreshAndReassessAsync(row, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task RefreshAndReassessAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		Say("▶ Re-assessing...");
		await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
		await dashboard.AssessLocalRepositoryAsync(
			row,
			cancellationToken,
			await CreateGitHubClientAsync().ConfigureAwait(false)).ConfigureAwait(false);
		cache.UpsertRow(row);
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
	/// <param name="cancellationToken">Stops the checks and the build before the commit; the push itself is not interruptible.</param>
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
			var push = await dashboard.CommitAndPushAsync(row, commitMessage, Say, cancellationToken).ConfigureAwait(false);

			// Whatever the outcome: a refused or failed push is as much part of the record as a good one.
			if (governance is not null)
			{
				await governance.RecordPushAsync(item, row.RepositoryFullName, push.Success, push.RefusalReason, row.LocalPath, CancellationToken.None).ConfigureAwait(false);
			}

			if (push.Success)
			{
				ReportPushed(item, row);
			}
			else
			{
				await HandleFailedPushAsync(item, row, push, cancellationToken).ConfigureAwait(false);
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

		var lines = await dashboard.GetWorkingTreeStatusPreviewAsync(row, maxLines: 3, CancellationToken.None).ConfigureAwait(false);
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
}
