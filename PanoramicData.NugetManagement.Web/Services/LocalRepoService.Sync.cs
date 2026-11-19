using System.Diagnostics;
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class LocalRepoService
{
	/// <summary>
	/// Runs dotnet build on the repository. The build skips restore for speed, but a repository that
	/// has never been restored (a fresh clone) has no assets file, and MSBuild fails on that alone —
	/// so that one failure is retried with a restore rather than reported as a broken repository.
	/// </summary>
	public async Task<(bool Success, string Output)> BuildAsync(
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken,
		bool restore)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Building in {Path}", path);

		if (restore)
		{
			// Asked for explicitly because what the project references has just changed.
			return await RunCommandWithStreamingAsync(path, "dotnet", ["build", "--verbosity", "normal"], onOutput, cancellationToken, null).ConfigureAwait(false);
		}

		var result = await RunCommandWithStreamingAsync(path, "dotnet", ["build", "--no-restore", "--verbosity", "normal"], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (result.Success || cancellationToken.IsCancellationRequested || !IsMissingRestoreFailure(result.Output))
		{
			return result;
		}

		_logger.LogInformation("Build in {Path} had no restore; restoring and rebuilding", path);
		onOutput?.Invoke("Nothing has been restored here yet; rebuilding with a restore.");
		return await RunCommandWithStreamingAsync(path, "dotnet", ["build", "--verbosity", "normal"], onOutput, cancellationToken, null).ConfigureAwait(false);
	}

	/// <summary>
	/// Whether a build failed only because the restore is absent (NETSDK1004) or for another framework
	/// (NETSDK1005, what a retarget leaves behind).
	/// </summary>
	internal static bool IsMissingRestoreFailure(string output)
		=> output.Contains("NETSDK1004", StringComparison.Ordinal)
			|| output.Contains("NETSDK1005", StringComparison.Ordinal);

	/// <summary>
	/// Runs a full dotnet build (with restore) on the repository. Used by the regression guard so a
	/// stale/absent restore never produces a false build failure (and a false rollback).
	/// </summary>
	public async Task<(bool Success, string Output)> BuildWithRestoreAsync(
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Building (with restore) in {Path}", path);
		return await RunCommandWithStreamingAsync(path, "dotnet", ["build", "--verbosity", "quiet"], onOutput, cancellationToken, null).ConfigureAwait(false);
	}

	/// <summary>
	/// Gets the most recent commits (hash + subject line), newest first.
	/// </summary>
	public async Task<IReadOnlyList<(string Hash, string Subject)>> GetRecentCommitsAsync(
		string repoIdentity,
		int count,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		var (ok, output) = await RunCommandWithStreamingAsync(
			path, "git", ["log", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "--pretty=format:%H%x1f%s"], null, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			return [];
		}

		var commits = new List<(string Hash, string Subject)>();
		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = line.Split('\u001f', 2);
			if (parts.Length == 2)
			{
				commits.Add((parts[0].Trim(), parts[1].Trim()));
			}
		}

		return commits;
	}

	/// <summary>
	/// Builds the repository at a specific commit (detached HEAD), always restoring the original
	/// branch afterwards. Used to confirm whether a build regression was introduced by our commits.
	/// </summary>
	public async Task<(bool Success, string Output)> BuildAtCommitAsync(
		string repoIdentity,
		string commitHash,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		var branch = await GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);

		var (ok, output) = await RunCommandWithStreamingAsync(path, "git", ["checkout", commitHash], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		try
		{
			// Use a full build (with restore) since dependencies may differ at the parent commit.
			return await RunCommandWithStreamingAsync(path, "dotnet", ["build", "--verbosity", "quiet"], onOutput, cancellationToken, null).ConfigureAwait(false);
		}
		finally
		{
			if (!string.IsNullOrWhiteSpace(branch))
			{
				await RunCommandWithStreamingAsync(path, "git", ["checkout", branch], onOutput, cancellationToken, null).ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// Reverts every commit after <paramref name="lastGoodCommitHash"/> up to HEAD as a single revert
	/// commit, then pushes. Used to automatically undo a regression we introduced. On revert conflict
	/// the revert is aborted and failure is returned (no partial state is pushed).
	/// </summary>
	public async Task<(bool Success, string Output)> RevertRangeAndPushAsync(
		string repoIdentity,
		string lastGoodCommitHash,
		string message,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);

		var (ok, output) = await RunCommandWithStreamingAsync(
			path, "git", ["revert", "--no-edit", "--no-commit", $"{lastGoodCommitHash}..HEAD"], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			await RunCommandWithStreamingAsync(path, "git", ["revert", "--abort"], onOutput, cancellationToken, null).ConfigureAwait(false);
			return (false, output);
		}

		(ok, output) = await RunCommandWithStreamingAsync(path, "git", ["commit", "-m", message], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		return await RunCommandWithStreamingAsync(path, "git", ["push"], onOutput, cancellationToken, null).ConfigureAwait(false);
	}

	/// <summary>
	/// Syncs a local repository with its remote: fetch, pull (rebase), push.
	/// </summary>
	public async Task<(bool Success, string Output)> GitSyncAsync(
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Git syncing {Path}", path);

		// Fetch
		var (ok, output) = await RunCommandWithStreamingAsync(path, "git", ["fetch", "--prune"], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Pull with rebase
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", ["pull", "--rebase"], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Push
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", ["push"], onOutput, cancellationToken, null).ConfigureAwait(false);
		return (ok, output);
	}

	/// <summary>
	/// Commits all local changes, fetches from origin, rebases, and pushes.
	/// </summary>
	public async Task<(bool Success, string Output)> CommitAndPushAsync(
		string repoIdentity,
		string commitMessage,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Commit and push in {Path}", path);

		// Stage all changes
		var (ok, output) = await RunCommandWithStreamingAsync(path, "git", ["add", "-A"], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Commit (allow empty commit to succeed gracefully)
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", ["commit", "-m", commitMessage], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			// Check if "nothing to commit" — that's fine, continue with fetch/rebase/push
			if (!output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
			{
				return (false, output);
			}
		}

		// Fetch
		(ok, output) = await RunNetworkGitWithRetryAsync(path, ["fetch", "--prune"], onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Rebase on top of remote
		(ok, output) = await RunNetworkGitWithRetryAsync(path, ["pull", "--rebase"], onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Push
		(ok, output) = await RunNetworkGitWithRetryAsync(path, ["push"], onOutput, cancellationToken).ConfigureAwait(false);
		return (ok, output);
	}

	/// <summary>
	/// How many times a git command that talks to the network is attempted before its failure stands.
	/// </summary>
	private const int _networkAttempts = 3;

	/// <summary>
	/// Runs a git command that talks to the network, trying again when it fails the way a flaky
	/// connection does.
	/// </summary>
	/// <remarks>
	/// The commit that precedes a push is local and stays local when the push fails, so a failure that
	/// would have cleared on its own leaves the clone ahead of origin with nothing to push it later.
	/// Only <see cref="IsTransientNetworkFailure"/> is retried: a refusal such as an archived repository
	/// or a rejected push is the same answer every time, and repeating it would only delay saying so.
	/// </remarks>
	internal static async Task<(bool Success, string Output)> RunNetworkGitWithRetryAsync(
		string workingDirectory,
		IReadOnlyList<string> arguments,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var attempt = 1;

		while (true)
		{
			var result = await RunCommandWithStreamingAsync(workingDirectory, "git", arguments, onOutput, cancellationToken, null).ConfigureAwait(false);

			if (result.Success || attempt >= _networkAttempts || !IsTransientNetworkFailure(result.Output))
			{
				return result;
			}

			onOutput?.Invoke($"↻ git {arguments[0]} failed on the network; retrying ({attempt + 1} of {_networkAttempts})...");
			await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
			attempt++;
		}
	}

	/// <summary>
	/// Whether a failed git command's output looks like a connection that may work if asked again.
	/// </summary>
	/// <param name="output">Everything the command printed.</param>
	/// <returns>True for name-resolution, timeout and dropped-connection failures; false for anything else.</returns>
	public static bool IsTransientNetworkFailure(string output)
		=> _transientNetworkSignals.Any(signal => output.Contains(signal, StringComparison.OrdinalIgnoreCase));

	private static readonly string[] _transientNetworkSignals =
	[
		"getaddrinfo",
		"Could not resolve host",
		"Connection timed out",
		"Connection reset",
		"Failed to connect to",
		"Recv failure",
		"RPC failed",
		"early EOF",
		"The remote end hung up unexpectedly"
	];
}
