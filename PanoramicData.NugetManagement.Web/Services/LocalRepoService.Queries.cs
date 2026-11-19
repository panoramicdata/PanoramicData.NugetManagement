using System.Diagnostics;
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class LocalRepoService
{
	/// <summary>
	/// Checks if a repository is cloned locally.
	/// </summary>
	public bool IsClonedLocally(string repoIdentity)
	{
		var path = GetLocalPath(repoIdentity);
		return Directory.Exists(Path.Combine(path, ".git"));
	}

	/// <summary>
	/// Finds the first .slnx file in a local repository, or null if none exists.
	/// </summary>
	public string? FindSlnxFile(string repoIdentity)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		try
		{
			return Directory.EnumerateFiles(path, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to search for .slnx in {Path}", path);
			return null;
		}
	}

	/// <summary>
	/// Gets the current branch name for a local repository.
	/// </summary>
	public async Task<string?> GetCurrentBranchAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);
		return exitCode == 0 ? output.Trim() : null;
	}

	/// <summary>
	/// Gets the commit HEAD points at, or null when the repository is not on disk or git fails.
	/// </summary>
	/// <remarks>
	/// Lets CQ-06 tell grades measured on the checked-out commit from grades measured on an older one.
	/// Null is a real answer — "we do not know" — and the rules keep the comparison unmade rather than
	/// reporting a staleness they cannot see.
	/// </remarks>
	public async Task<string?> GetHeadShaAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);

		return exitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
	}

	/// <summary>
	/// Gets the remote default branch name for origin when available.
	/// </summary>
	public async Task<string?> GetRemoteDefaultBranchAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		_ = await RunCommandAsync(path, "git", ["fetch", "--prune", "origin"], cancellationToken).ConfigureAwait(false);
		_ = await RunCommandAsync(path, "git", ["remote", "set-head", "origin", "-a"], cancellationToken).ConfigureAwait(false);

		var (showExitCode, showOutput) = await RunCommandAsync(path, "git", ["remote", "show", "origin"], cancellationToken).ConfigureAwait(false);
		if (showExitCode == 0 && HeadBranchFromRemoteShow(showOutput) is { } remoteHeadBranch)
		{
			return remoteHeadBranch;
		}

		var (headExitCode, headOutput) = await RunCommandAsync(path, "git", ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], cancellationToken).ConfigureAwait(false);
		return headExitCode == 0 ? BranchFromSymbolicRef(headOutput) : null;
	}

	private static string? HeadBranchFromRemoteShow(string showOutput)
	{
		var headBranchLine = showOutput
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
			.FirstOrDefault(line => line.Contains("HEAD branch:", StringComparison.OrdinalIgnoreCase));

		var idx = headBranchLine?.IndexOf(':') ?? -1;
		if (headBranchLine is null || idx < 0)
		{
			return null;
		}

		var remoteHeadBranch = headBranchLine[(idx + 1)..].Trim();
		return string.IsNullOrWhiteSpace(remoteHeadBranch)
			|| string.Equals(remoteHeadBranch, "(unknown)", StringComparison.OrdinalIgnoreCase)
				? null
				: remoteHeadBranch;
	}

	private static string? BranchFromSymbolicRef(string headOutput)
	{
		var full = headOutput.Trim();
		if (string.IsNullOrWhiteSpace(full))
		{
			return null;
		}

		return full.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
			? full["origin/".Length..]
			: full;
	}

	/// <summary>
	/// Checks whether the working tree is clean.
	/// </summary>
	public async Task<bool?> IsWorkingTreeCleanAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
		if (exitCode != 0)
		{
			_logger.LogWarning("Failed to determine working tree cleanliness for {RepoName}. git status exit code: {ExitCode}", repoIdentity, exitCode);
			return null;
		}

		return string.IsNullOrWhiteSpace(output);
	}

	/// <summary>
	/// Gets a preview of git status porcelain lines for dirty working tree diagnostics.
	/// </summary>
	public async Task<IReadOnlyList<string>> GetWorkingTreeStatusPreviewAsync(
		string repoIdentity,
		int maxLines,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path) || maxLines <= 0)
		{
			return [];
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
		if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
		{
			return [];
		}

		return output
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
			.Take(maxLines)
			.ToArray();
	}

	/// <summary>
	/// Returns the latest git tag reachable from HEAD (e.g. "1.0.55").
	/// Returns null if the repo has no tags or the command fails.
	/// </summary>
	public async Task<string?> GetLatestTagAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", ["describe", "--tags", "--abbrev=0"], cancellationToken).ConfigureAwait(false);
		if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
		{
			return null;
		}

		return output.Trim();
	}

	/// <summary>
	/// Returns the version Nerdbank.GitVersioning would give a release built from this clone as it
	/// stands, or null when that cannot be established.
	/// </summary>
	/// <param name="repoIdentity">The repository.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <remarks>
	/// Asks nbgv rather than computing the height here, because its rules — versionHeightOffset, which
	/// refs count as public releases, when the height resets — are subtle enough that an independent
	/// calculation would eventually disagree with what a release actually publishes, and a number that
	/// disagrees is worse than no number in an informational finding. Every failure is null for the
	/// same reason: no nbgv on the machine, no version.json, a repository it will not read.
	/// </remarks>
	public async Task<string?> GetNextVersionAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var executable = NbgvVersionReader.ResolveExecutable(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			File.Exists);

		try
		{
			var (exitCode, output) = await RunCommandAsync(
				path, executable, ["get-version", "-f", "json"], cancellationToken).ConfigureAwait(false);

			return exitCode == 0 ? NbgvVersionReader.ReadVersion(output) : null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// A machine without nbgv fails at the launch itself rather than with an exit code.
			_logger.LogDebug(ex, "Could not read the nbgv version for {Repo}.", repoIdentity);
			return null;
		}
	}

	/// <summary>
	/// Checks whether the local branch has commits that origin has not, without contacting origin.
	/// </summary>
	/// <param name="repoIdentity">The repository.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>
	/// True when HEAD is ahead of the remote-tracking ref, false when it is not, and null when there
	/// is no clone, no remote-tracking ref, or git would not answer.
	/// </returns>
	/// <remarks>
	/// Deliberately no fetch, unlike <see cref="IsSyncedWithOriginAsync"/>: whether this clone holds
	/// commits nobody else has is answerable from the refs already on disk, and the answer cannot go
	/// stale behind the app's back the way "origin has moved on" can — only something done to this
	/// checkout changes it. That makes it cheap enough to re-read on a few-second cycle, which is what
	/// lets Commit &amp; Push grey itself out the moment there is nothing left to send.
	/// </remarks>
	public async Task<bool?> HasUnpushedCommitsAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		// @{u} is the branch's own upstream, whatever it is called — not an assumption that it is
		// origin/<same name>. A detached HEAD or a branch never pushed has none, and git exits
		// non-zero, which is the null answer rather than a false one.
		var (exitCode, output) = await RunCommandAsync(
			path, "git", ["rev-list", "--count", "@{u}..HEAD"], cancellationToken).ConfigureAwait(false);
		if (exitCode != 0 || !int.TryParse(output.Trim(), out var ahead))
		{
			return null;
		}

		return ahead > 0;
	}

	/// <summary>
	/// Checks whether the local branch is in sync with its origin counterpart.
	/// Performs a git fetch first, then compares HEAD against origin/{branch}.
	/// Returns true if HEAD matches origin/{branch} (not behind and not ahead).
	/// </summary>
	public async Task<bool?> IsSyncedWithOriginAsync(string repoIdentity, CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		// Fetch latest from origin
		var (fetchExit, _) = await RunCommandAsync(path, "git", ["fetch", "--prune"], cancellationToken).ConfigureAwait(false);
		if (fetchExit != 0)
		{
			return null;
		}

		// Get current branch
		var (branchExit, branchOutput) = await RunCommandAsync(path, "git", ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);
		if (branchExit != 0)
		{
			return null;
		}

		var branch = branchOutput.Trim();

		// Behind, then ahead of origin; either failing means the remote tracking branch may not exist.
		var behind = await CountCommitsAsync(path, $"HEAD..origin/{branch}", cancellationToken).ConfigureAwait(false);
		var ahead = behind is null ? null : await CountCommitsAsync(path, $"origin/{branch}..HEAD", cancellationToken).ConfigureAwait(false);
		if (behind is null || ahead is null)
		{
			return null;
		}

		return behind == 0 && ahead == 0;
	}

	private static async Task<int?> CountCommitsAsync(string path, string range, CancellationToken cancellationToken)
	{
		var (exit, output) = await RunCommandAsync(path, "git", ["rev-list", "--count", range], cancellationToken).ConfigureAwait(false);
		if (exit != 0)
		{
			return null;
		}

		return int.TryParse(output.Trim(), out var count) ? count : -1;
	}
}
