namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// <see cref="IAiChangeGit"/> over a clone on disk.
/// </summary>
/// <param name="clonePath">The clone's working directory.</param>
/// <param name="expectedIdentity">
/// The repository, as <c>owner/name</c>, that the clone must hold. Null skips the check, which is for
/// tests against a local remote that has no GitHub identity.
/// </param>
public sealed class LocalRepoAiChangeGit(string clonePath, string? expectedIdentity = null) : IAiChangeGit
{
	/// <summary>
	/// Confirms the clone's origin is the repository the caller thinks it is.
	/// </summary>
	/// <remarks>
	/// The pipeline pushes, opens a pull request and merges against the caller's repository, on the
	/// strength of tests run in this directory. A directory holding some other repository would make that
	/// evidence about the wrong code, so it is refused before anything is created.
	/// </remarks>
	private async Task<string?> IdentityRefusalAsync(CancellationToken cancellationToken)
	{
		if (expectedIdentity is null)
		{
			return null;
		}

		var (ok, url) = await GitAsync(["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false);
		var actual = ok ? LocalRepoService.ParseRepoIdentity(url) : null;

		if (actual is null)
		{
			return $"the clone's origin is not a GitHub remote, so it cannot be confirmed to hold {expectedIdentity}.";
		}

		return string.Equals(actual, expectedIdentity, StringComparison.OrdinalIgnoreCase)
			? null
			: $"the clone holds {actual}, not {expectedIdentity}.";
	}

	/// <inheritdoc />
	public async Task<string?> RefusalAsync(CancellationToken cancellationToken)
	{
		if (await IdentityRefusalAsync(cancellationToken).ConfigureAwait(false) is { } identityProblem)
		{
			return identityProblem;
		}

		var (ok, branch) = await GitAsync(["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);

		if (!ok || branch.Trim() != "main")
		{
			return $"the clone is on '{branch.Trim()}', not main.";
		}

		var (statusOk, status) = await GitAsync(["status", "--porcelain"], cancellationToken).ConfigureAwait(false);

		if (!statusOk || status.Trim().Length > 0)
		{
			return "the clone has uncommitted changes.";
		}

		(ok, var output) = await LocalRepoService
			.RunNetworkGitWithRetryAsync(clonePath, ["pull", "--ff-only"], null, cancellationToken)
			.ConfigureAwait(false);

		return ok ? null : $"main could not be fast-forwarded: {output}";
	}

	/// <inheritdoc />
	public async Task<string?> DeliverRefusalAsync(IReadOnlySet<string> ownedPaths, CancellationToken cancellationToken)
	{
		if (await IdentityRefusalAsync(cancellationToken).ConfigureAwait(false) is { } identityProblem)
		{
			return identityProblem;
		}

		var (ok, branch) = await GitAsync(["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);

		if (!ok || branch.Trim() != "main")
		{
			return $"the clone is on '{branch.Trim()}', not main.";
		}

		// -uall lists the files inside an untracked folder, so one stray file cannot hide behind a folder
		// that also holds files the caller wrote.
		var (statusOk, status) = await GitAsync(["status", "--porcelain", "-uall"], cancellationToken).ConfigureAwait(false);

		if (!statusOk)
		{
			return "the clone's status could not be read.";
		}

		var owned = ownedPaths.Select(NormalisePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

		var foreign = status
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(line => line.TrimEnd('\r'))
			.Where(line => line.Length > 3)
			.Select(line => StatusPath(line[3..]))
			.Where(path => !owned.Contains(path))
			.ToList();

		return foreign.Count == 0
			? null
			: $"the clone has uncommitted changes this session did not make: {string.Join(", ", foreign)}.";
	}

	private static string StatusPath(string porcelainPath)
	{
		var path = porcelainPath.Contains(" -> ", StringComparison.Ordinal)
			? porcelainPath[(porcelainPath.IndexOf(" -> ", StringComparison.Ordinal) + 4)..]
			: porcelainPath;

		return NormalisePath(path.Trim('"'));
	}

	private static string NormalisePath(string path) => path.Replace('\\', '/');

	/// <inheritdoc />
	public async Task CreateBranchAsync(string branch, CancellationToken cancellationToken)
		=> await RequireAsync(["checkout", "-b", branch], cancellationToken).ConfigureAwait(false);

	/// <inheritdoc />
	public async Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(CancellationToken cancellationToken)
	{
		// Untracked files are added intent-to-add so a brand-new test file shows up in the diff.
		await GitAsync(["add", "-N", "."], cancellationToken).ConfigureAwait(false);

		// Two reads, because they answer different questions. The names are every path touched, and are
		// what the allowlist and cap judge: a deleted file, a binary and a renamed file have no new-side
		// lines, so the line diff cannot see them. --no-renames makes a rename a delete plus an add, so
		// both paths show. The line diff is only for which lines changed, for coverage.
		var names = await RequireAsync(
			["-c", "core.quotepath=false", "diff", "--name-only", "--no-renames", "main"],
			cancellationToken).ConfigureAwait(false);

		var diff = await RequireAsync(["diff", "-U0", "--no-renames", "main"], cancellationToken).ConfigureAwait(false);

		var withLines = ChangedLineParser.Parse(diff).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);

		return
		[
			.. names
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Select(name => name.TrimEnd('\r'))
				.Select(name => withLines.TryGetValue(name, out var file) ? file : new ChangedFile(name, new HashSet<int>()))
		];
	}

	/// <inheritdoc />
	public async Task<string> CommitAsync(string message, CancellationToken cancellationToken)
	{
		await RequireAsync(["add", "-A"], cancellationToken).ConfigureAwait(false);
		await RequireAsync(["commit", "-m", message], cancellationToken).ConfigureAwait(false);

		return (await RequireAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).Trim();
	}

	/// <inheritdoc />
	public async Task PushBranchAsync(string branch, CancellationToken cancellationToken)
	{
		var (ok, output) = await LocalRepoService
			.RunNetworkGitWithRetryAsync(clonePath, ["push", "-u", "origin", branch], null, cancellationToken)
			.ConfigureAwait(false);

		if (!ok)
		{
			throw new InvalidOperationException($"git push failed: {output}");
		}
	}

	/// <inheritdoc />
	public async Task<string?> ReturnToMainAsync(string branch, bool deleteRemoteBranch, CancellationToken cancellationToken)
	{
		var problems = new List<string>();

		var (reset, resetText) = await GitAsync(["reset", "--hard"], cancellationToken).ConfigureAwait(false);

		if (!reset)
		{
			problems.Add($"could not discard the working changes: {resetText.Trim()}");
		}

		var (cleaned, cleanText) = await GitAsync(["clean", "-fd"], cancellationToken).ConfigureAwait(false);

		if (!cleaned)
		{
			problems.Add($"could not remove untracked files: {cleanText.Trim()}");
		}

		var (onMain, checkoutText) = await GitAsync(["checkout", "main"], cancellationToken).ConfigureAwait(false);

		if (!onMain)
		{
			// Once more after a moment: on Windows a test host that has only just exited can still hold a
			// file that the checkout needs to replace.
			await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
			(onMain, checkoutText) = await GitAsync(["checkout", "main"], cancellationToken).ConfigureAwait(false);
		}

		if (!onMain)
		{
			problems.Add($"could not return to main, so the clone is still on {branch}: {checkoutText.Trim()}");
		}
		else
		{
			var (deleted, deleteText) = await GitAsync(["branch", "-D", branch], cancellationToken).ConfigureAwait(false);

			if (!deleted)
			{
				problems.Add($"the local branch {branch} could not be deleted: {deleteText.Trim()}");
			}
		}

		if (deleteRemoteBranch)
		{
			var (remoteDeleted, remoteText) = await LocalRepoService
				.RunNetworkGitWithRetryAsync(clonePath, ["push", "origin", "--delete", branch], null, cancellationToken)
				.ConfigureAwait(false);

			if (!remoteDeleted)
			{
				problems.Add($"the branch {branch} could not be deleted on origin: {remoteText.Trim()}");
			}
		}

		await LocalRepoService
			.RunNetworkGitWithRetryAsync(clonePath, ["pull", "--ff-only"], null, cancellationToken)
			.ConfigureAwait(false);

		return problems.Count == 0 ? null : string.Join(" ", problems);
	}

	private Task<(bool Success, string Output)> GitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
		=> LocalRepoService.RunCommandWithStreamingAsync(clonePath, "git", arguments, null, cancellationToken);

	private async Task<string> RequireAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		var (ok, output) = await GitAsync(arguments, cancellationToken).ConfigureAwait(false);

		return ok
			? output
			: throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}");
	}
}
