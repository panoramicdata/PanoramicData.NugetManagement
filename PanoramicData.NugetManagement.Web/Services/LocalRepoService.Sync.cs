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
	public async Task<IReadOnlyList<GitCommit>> GetRecentCommitsAsync(
		string repoIdentity,
		int count,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		var format = $"--pretty=format:%H%x1f%s%x1f%(trailers:key={GovernanceCommitMessage.TrailerKey},valueonly,separator=%x2C)%x1e";
		var (ok, output) = await RunCommandWithStreamingAsync(
			path, "git", ["log", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture), format], null, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			return [];
		}

		var commits = new List<GitCommit>();
		foreach (var record in output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = record.Split('\u001f');
			if (parts.Length == 3)
			{
				var trailer = parts[2].Trim();
				commits.Add(new GitCommit(parts[0].Trim(), parts[1].Trim(), trailer.Length == 0 ? null : trailer));
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
	/// Brings a local repository up to date with its remote: fetch, then fast-forward. Never pushes;
	/// local commits that origin lacks are reported and left for Commit &amp; push to deliver as a pull
	/// request.
	/// </summary>
	public async Task<(bool Success, string Output)> GitSyncAsync(
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Git syncing {Path}", path);

		var (ok, output) = await RunNetworkGitWithRetryAsync(path, ["fetch", "--prune"], onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Fast-forward only: a rebase would replay local commits, which only a pull request may deliver.
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", ["pull", "--ff-only"], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		var ahead = await CountCommitsAsync(path, "@{u}..HEAD", cancellationToken).ConfigureAwait(false);
		if (ahead > 0)
		{
			onOutput?.Invoke($"ℹ️ {ahead} local commit(s) are not on origin. Nothing was pushed: Commit & push will deliver them as a pull request.");
		}

		return (true, output);
	}

	private async Task<string> DescribeStagedChangeAsync(
		string path,
		string commitSubject,
		IReadOnlyCollection<string> ruleIds,
		CancellationToken cancellationToken)
	{
		var (namesOk, names) = await RunCommandWithStreamingAsync(path, "git", ["diff", "--cached", "--name-only"], null, cancellationToken, null).ConfigureAwait(false);
		string[] projectFiles = ["*.csproj", "*.props", "*.targets"];
		var (diffOk, diff) = await RunCommandWithStreamingAsync(path, "git", ["diff", "--cached", "-U0", "--", .. projectFiles], null, cancellationToken, null).ConfigureAwait(false);

		var files = namesOk
			? names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			: [];
		var moves = diffOk ? GovernanceCommitMessage.PackageMoves(diff) : [];

		return GovernanceCommitMessage.Compose(commitSubject, ruleIds, files, moves);
	}

	/// <summary>
	/// Commits all local changes to a new governance branch, carrying any local commits origin lacks, and
	/// pushes that branch. Main is never pushed: local main goes back to origin's, and the pull request
	/// opened from the branch is how the change reaches it.
	/// </summary>
	/// <param name="repoIdentity">The repository, as owner/name.</param>
	/// <param name="commitSubject">The commit subject; the body and trailer are added from what is staged.</param>
	/// <param name="ruleIds">The rules the change applied, for the commit body and trailer.</param>
	/// <param name="onOutput">Receives the git output.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<BranchDelivery> CommitToPullRequestBranchAsync(
		string repoIdentity,
		string commitSubject,
		IReadOnlyCollection<string> ruleIds,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Committing to a pull request branch in {Path}", path);

		var current = await GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);
		if (!string.Equals(current, MainBranch, StringComparison.Ordinal))
		{
			return BranchDelivery.Failed($"Commit & push delivers from {MainBranch}, but {repoIdentity} has '{current}' checked out.");
		}

		var (ok, output) = await RunCommandWithStreamingAsync(path, "git", ["add", "-A"], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			return BranchDelivery.Failed(output);
		}

		var (staged, _) = await RunCommandAsync(path, "git", ["diff", "--cached", "--quiet"], cancellationToken).ConfigureAwait(false);
		var message = staged == 1 ? await DescribeStagedChangeAsync(path, commitSubject, ruleIds, cancellationToken).ConfigureAwait(false) : null;

		(ok, output) = await RunNetworkGitWithRetryAsync(path, ["fetch", "--prune"], onOutput, cancellationToken).ConfigureAwait(false);
		return ok
			? await CommitOnNewBranchAndPushAsync(path, NewGovernanceBranchName(), message, onOutput, cancellationToken).ConfigureAwait(false)
			: BranchDelivery.Failed(output);
	}

	private const string MainBranch = "main";

	private const string OriginMain = "origin/main";

	/// <summary>A unique branch name such as <c>gov/20261128-120000-a1b2c3</c>.</summary>
	internal static string NewGovernanceBranchName()
		=> $"gov/{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..6]}";

	private async Task<BranchDelivery> CommitOnNewBranchAndPushAsync(
		string path,
		string branch,
		string? message,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		// The branch starts at HEAD, so local commits origin lacks come along with the new one.
		var (ok, output) = await RunCommandWithStreamingAsync(path, "git", ["checkout", "-b", branch], onOutput, cancellationToken, null).ConfigureAwait(false);
		if (!ok)
		{
			return BranchDelivery.Failed(output);
		}

		if (message is not null)
		{
			(ok, output) = await RunCommandWithStreamingAsync(path, "git", ["commit", "-m", message], onOutput, cancellationToken, null).ConfigureAwait(false);
			if (!ok)
			{
				await AbandonBranchAsync(path, branch, keepCommits: false, cancellationToken).ConfigureAwait(false);
				return BranchDelivery.Failed(output);
			}
		}

		var ahead = await CountCommitsAsync(path, $"{OriginMain}..HEAD", cancellationToken).ConfigureAwait(false);
		if (ahead is not > 0)
		{
			await AbandonBranchAsync(path, branch, keepCommits: true, cancellationToken).ConfigureAwait(false);
			onOutput?.Invoke(ahead == 0 ? $"ℹ️ Nothing to deliver: the clone has nothing {OriginMain} lacks." : $"❌ Could not compare with {OriginMain}.");
			return ahead == 0 ? BranchDelivery.Nothing : BranchDelivery.Failed($"Could not compare with {OriginMain}.");
		}

		return await PushBranchAsync(path, branch, onOutput, cancellationToken).ConfigureAwait(false);
	}

	private async Task<BranchDelivery> PushBranchAsync(string path, string branch, Action<string>? onOutput, CancellationToken cancellationToken)
	{
		var delivery = await DescribeHeadAsync(path, branch, cancellationToken).ConfigureAwait(false);

		var (ok, output) = await RunNetworkGitWithRetryAsync(path, ["push", "-u", "origin", branch], onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			// Back onto main, so the commit stays visible as unpushed and the next attempt carries it.
			await AbandonBranchAsync(path, branch, keepCommits: true, cancellationToken).ConfigureAwait(false);
			return BranchDelivery.Failed(output);
		}

		await ReturnMainToOriginAsync(path, branch, onOutput, cancellationToken).ConfigureAwait(false);
		return delivery;
	}

	private static async Task<BranchDelivery> DescribeHeadAsync(string path, string branch, CancellationToken cancellationToken)
	{
		var (_, sha) = await RunCommandAsync(path, "git", ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
		var (_, subject) = await RunCommandAsync(path, "git", ["log", "-1", "--format=%s"], cancellationToken).ConfigureAwait(false);
		var (_, message) = await RunCommandAsync(path, "git", ["log", "-1", "--format=%B"], cancellationToken).ConfigureAwait(false);

		return new BranchDelivery
		{
			Success = true,
			Branch = branch,
			HeadSha = sha.Trim(),
			Subject = subject.Trim(),
			Message = message.Trim()
		};
	}

	/// <summary>
	/// Goes back to main without the branch. With <paramref name="keepCommits"/> main fast-forwards to
	/// it first, so nothing committed is lost.
	/// </summary>
	private static async Task AbandonBranchAsync(string path, string branch, bool keepCommits, CancellationToken cancellationToken)
	{
		await RunCommandAsync(path, "git", ["checkout", MainBranch], cancellationToken).ConfigureAwait(false);

		if (keepCommits)
		{
			await RunCommandAsync(path, "git", ["merge", "--ff-only", branch], cancellationToken).ConfigureAwait(false);
		}

		await RunCommandAsync(path, "git", ["branch", "-D", branch], cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Puts local main back on origin's main once the branch holding its commits is on origin.
	/// </summary>
	/// <remarks>
	/// <c>reset --keep</c> rather than <c>--hard</c>: it keeps unrelated uncommitted edits, and refuses
	/// rather than overwrite one. The local branch goes because its remote-tracking ref keeps the commit.
	/// </remarks>
	private static async Task ReturnMainToOriginAsync(string path, string branch, Action<string>? onOutput, CancellationToken cancellationToken)
	{
		var (checkout, checkoutOutput) = await RunCommandAsync(path, "git", ["checkout", MainBranch], cancellationToken).ConfigureAwait(false);
		var (reset, resetOutput) = checkout == 0
			? await RunCommandAsync(path, "git", ["reset", "--keep", OriginMain], cancellationToken).ConfigureAwait(false)
			: (checkout, checkoutOutput);

		if (reset != 0)
		{
			onOutput?.Invoke($"⚠️ {branch} is on origin, but local {MainBranch} could not be put back on {OriginMain}: {resetOutput.Trim()}");
			return;
		}

		await RunCommandAsync(path, "git", ["branch", "-D", branch], cancellationToken).ConfigureAwait(false);
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
