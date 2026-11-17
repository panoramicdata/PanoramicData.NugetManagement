using System.Diagnostics;
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class LocalRepoService
{
	/// <summary>
	/// Counts the commits on <c>origin</c> that this checkout does not have, fetching first. Returns null
	/// if that cannot be established.
	/// </summary>
	/// <remarks>
	/// Deliberately only "behind", not <see cref="IsSyncedWithOriginAsync"/>'s "behind or ahead": having
	/// local commits to push is the normal state at the point this matters, whereas missing commits means
	/// whatever is about to be committed was decided against a repository that has since moved on.
	/// </remarks>
	public async Task<int?> CountCommitsBehindOriginAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (fetchExit, _) = await RunCommandAsync(path, "git", ["fetch", "--prune"], cancellationToken).ConfigureAwait(false);
		if (fetchExit != 0)
		{
			return null;
		}

		var (branchExit, branchOutput) = await RunCommandAsync(path, "git", ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);
		if (branchExit != 0)
		{
			return null;
		}

		var branch = branchOutput.Trim();
		var (behindExit, behindOutput) = await RunCommandAsync(
			path, "git", ["rev-list", "--count", $"HEAD..origin/{branch}"], cancellationToken).ConfigureAwait(false);

		// A missing remote-tracking branch fails here, and is indistinguishable from a real error.
		return behindExit == 0 && int.TryParse(behindOutput.Trim(), out var behind) ? behind : null;
	}

	/// <summary>
	/// Brings a clone level with <c>origin</c> when it is only behind, fetching first. Returns how many
	/// commits it was behind, or null when that cannot be established.
	/// </summary>
	/// <remarks>
	/// Fast-forward only: a clone with commits of its own is left exactly as it was and reported as not
	/// caught up, because combining histories is a decision, not a catch-up. The caller is expected to
	/// have checked the working tree is clean.
	/// </remarks>
	/// <param name="repoIdentity">The repository, as "owner/name".</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<(bool CaughtUp, int? Behind)> FastForwardToOriginAsync(
		string repoIdentity,
		CancellationToken cancellationToken)
	{
		var behind = await CountCommitsBehindOriginAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		if (behind is not > 0)
		{
			return (behind == 0, behind);
		}

		var path = GetLocalPath(repoIdentity);
		var (branchExit, branchOutput) = await RunCommandAsync(path, "git", ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);
		if (branchExit != 0)
		{
			return (false, behind);
		}

		var (mergeExit, _) = await RunCommandAsync(
			path, "git", ["merge", "--ff-only", $"origin/{branchOutput.Trim()}"], cancellationToken).ConfigureAwait(false);

		return (mergeExit == 0, behind);
	}

	/// <summary>
	/// Throws away everything uncommitted in a clone: modifications to tracked files, and untracked files
	/// and directories. Returns the <c>git status</c> lines that were discarded.
	/// </summary>
	/// <remarks>
	/// Both halves are needed, because a remediation does not only edit files — it adds them (a
	/// <c>xunit.runner.json</c>, a <c>global.json</c>), and <c>reset --hard</c> alone would leave those
	/// behind to be swept into the next commit. Safe here only because this runs in a clone of the app's
	/// own making, which is the reason those live somewhere separate from the user's checkouts.
	/// </remarks>
	public async Task<(bool Success, IReadOnlyList<string> Discarded)> DiscardLocalChangesAsync(
		string repoIdentity,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return (false, []);
		}

		// Captured first: afterwards there is nothing left to report.
		var discarded = await GetWorkingTreeStatusPreviewAsync(repoIdentity, int.MaxValue, cancellationToken)
			.ConfigureAwait(false);

		var (resetExit, _) = await RunCommandAsync(path, "git", ["reset", "--hard", "HEAD"], cancellationToken).ConfigureAwait(false);
		var (cleanExit, _) = await RunCommandAsync(path, "git", ["clean", "-fd"], cancellationToken).ConfigureAwait(false);

		_logger.LogInformation("Discarded {Count} local change(s) in {Path}", discarded.Count, path);
		return (resetExit == 0 && cleanExit == 0, discarded);
	}

	/// <summary>
	/// Sets aside everything uncommitted, untracked files included, in a git stash, leaving the tree clean.
	/// </summary>
	/// <param name="repoIdentity">The repository.</param>
	/// <param name="message">The stash's label, which is how a person finds it again in <c>git stash list</c>.</param>
	/// <param name="cancellationToken">Signalled when the work is stopped.</param>
	/// <remarks>
	/// Preferred to <see cref="DiscardLocalChangesAsync"/> wherever the changes were made by a model or a
	/// remediation nobody has reviewed: setting work aside costs a line in the stash list, and throwing it
	/// away cannot be taken back. Returns false, with git's output, when there was nothing to stash.
	/// </remarks>
	public async Task<(bool Success, string Output)> StashLocalChangesAsync(
		string repoIdentity,
		string message,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return (false, "The clone does not exist.");
		}

		var (exit, output) = await RunCommandAsync(path, "git", ["stash", "push", "--include-untracked", "-m", message], cancellationToken)
			.ConfigureAwait(false);

		_logger.LogInformation("Stashed local changes in {Path}: {Output}", path, output.Trim());

		return (exit == 0 && !output.Contains("No local changes", StringComparison.OrdinalIgnoreCase), output);
	}

	/// <summary>
	/// Records everything uncommitted, untracked files included, as an entry in the stash list while leaving the
	/// working tree exactly as it is.
	/// </summary>
	/// <param name="repoIdentity">The repository.</param>
	/// <param name="message">The entry's label.</param>
	/// <param name="cancellationToken">Signalled when the work is stopped.</param>
	/// <returns>Whether an entry was made; false when there was nothing to record.</returns>
	/// <remarks>
	/// A safety net for work that is about to go through something that resets the tree if it fails: the
	/// changes are recoverable afterwards whatever happens. <c>git stash create</c> ignores untracked files, so
	/// they are staged for the instant it takes and unstaged again; the files themselves are never touched.
	/// </remarks>
	public async Task<bool> SnapshotLocalChangesAsync(
		string repoIdentity,
		string message,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return false;
		}

		await RunCommandAsync(path, "git", ["add", "-A"], cancellationToken).ConfigureAwait(false);

		try
		{
			var (createExit, sha) = await RunCommandAsync(path, "git", ["stash", "create"], cancellationToken).ConfigureAwait(false);
			var commit = sha.Trim();

			if (createExit != 0 || commit.Length != 40)
			{
				return false;
			}

			var (storeExit, _) = await RunCommandAsync(path, "git", ["stash", "store", "-m", message, commit], cancellationToken).ConfigureAwait(false);
			return storeExit == 0;
		}
		finally
		{
			await RunCommandAsync(path, "git", ["reset", "-q"], CancellationToken.None).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Removes the stash entry <see cref="SnapshotLocalChangesAsync"/> made, once what it protected is safe.
	/// </summary>
	/// <param name="repoIdentity">The repository.</param>
	/// <param name="message">The label the entry was made with.</param>
	/// <param name="cancellationToken">Signalled when the work is stopped.</param>
	public async Task DropSnapshotAsync(string repoIdentity, string message, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		var (exit, output) = await RunCommandAsync(path, "git", ["stash", "list", "--format=%gd|%gs"], cancellationToken).ConfigureAwait(false);

		if (exit != 0)
		{
			return;
		}

		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = line.Trim().Split('|', 2);

			if (parts.Length == 2 && parts[1].EndsWith(message, StringComparison.Ordinal))
			{
				await RunCommandAsync(path, "git", ["stash", "drop", parts[0]], cancellationToken).ConfigureAwait(false);
				return;
			}
		}
	}

	/// <summary>
	/// Brings back the most recent stash, which is the one <see cref="StashLocalChangesAsync"/> made.
	/// </summary>
	/// <param name="repoIdentity">The repository.</param>
	/// <param name="cancellationToken">Signalled when the work is stopped.</param>
	public async Task<(bool Success, string Output)> PopStashAsync(
		string repoIdentity,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		var (exit, output) = await RunCommandAsync(path, "git", ["stash", "pop"], cancellationToken).ConfigureAwait(false);

		return (exit == 0, output);
	}

	/// <summary>
	/// Puts one file back as the last commit has it, or removes it when the commit does not have it.
	/// </summary>
	/// <param name="repoIdentity">The repository.</param>
	/// <param name="relativePath">The file, relative to the clone root.</param>
	/// <param name="cancellationToken">Signalled when the work is stopped.</param>
	/// <returns>A sentence saying what happened, in the form a model reads back as a tool result.</returns>
	public async Task<string> RestoreFileAsync(
		string repoIdentity,
		string relativePath,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);

		var (trackedExit, _) = await RunCommandAsync(path, "git", ["ls-files", "--error-unmatch", "--", relativePath], cancellationToken)
			.ConfigureAwait(false);

		if (trackedExit == 0)
		{
			var (exit, output) = await RunCommandAsync(path, "git", ["checkout", "HEAD", "--", relativePath], cancellationToken)
				.ConfigureAwait(false);

			return exit == 0
				? $"Restored {relativePath} to the last commit."
				: $"Could not restore {relativePath}: {output.Trim()}";
		}

		var full = Path.GetFullPath(Path.Combine(path, relativePath));
		if (File.Exists(full))
		{
			File.Delete(full);
			return $"Removed {relativePath}: it is not in the last commit.";
		}

		return $"{relativePath} is not in the last commit and does not exist, so there is nothing to restore.";
	}

	/// <summary>
	/// Opens a folder in this machine's file manager. Returns false, with the reason, if it cannot be.
	/// </summary>
	/// <remarks>
	/// Runs on the server, which is the user's own machine while the dashboard is run locally — the same
	/// assumption "Open in IDE" already makes. <c>UseShellExecute</c> hands the path to the shell rather
	/// than naming a file manager, so there is nothing platform-specific to get wrong.
	/// </remarks>
	public (bool Success, string Message) OpenFolder(string path)
	{
		if (!Path.IsPathRooted(path))
		{
			return (false, $"{path} is not a rooted path.");
		}

		if (!Directory.Exists(path))
		{
			return (false, $"{path} does not exist yet.");
		}

		try
		{
			using var process = Process.Start(new ProcessStartInfo // nosemgrep
			{
				FileName = path, // nosemgrep
				UseShellExecute = true
			});

			_logger.LogInformation("Opened folder {Path} in the file manager", path);
			return (true, path);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not open folder {Path}", path);
			return (false, ex.Message);
		}
	}
}
