namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// <see cref="IAiChangeGit"/> over a clone on disk.
/// </summary>
/// <param name="clonePath">The clone's working directory.</param>
public sealed class LocalRepoAiChangeGit(string clonePath) : IAiChangeGit
{
	/// <inheritdoc />
	public async Task<string?> RefusalAsync(CancellationToken cancellationToken)
	{
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
	public async Task CreateBranchAsync(string branch, CancellationToken cancellationToken)
		=> await RequireAsync(["checkout", "-b", branch], cancellationToken).ConfigureAwait(false);

	/// <inheritdoc />
	public async Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(CancellationToken cancellationToken)
	{
		// Untracked files are added intent-to-add so a brand-new test file shows up in the diff.
		await GitAsync(["add", "-N", "."], cancellationToken).ConfigureAwait(false);

		var diff = await RequireAsync(["diff", "-U0", "main"], cancellationToken).ConfigureAwait(false);

		return ChangedLineParser.Parse(diff);
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
	public async Task ReturnToMainAsync(string branch, bool deleteRemoteBranch, CancellationToken cancellationToken)
	{
		await GitAsync(["reset", "--hard"], cancellationToken).ConfigureAwait(false);
		await GitAsync(["clean", "-fd"], cancellationToken).ConfigureAwait(false);
		await GitAsync(["checkout", "main"], cancellationToken).ConfigureAwait(false);
		await GitAsync(["branch", "-D", branch], cancellationToken).ConfigureAwait(false);

		if (deleteRemoteBranch)
		{
			await LocalRepoService
				.RunNetworkGitWithRetryAsync(clonePath, ["push", "origin", "--delete", branch], null, cancellationToken)
				.ConfigureAwait(false);
		}

		await LocalRepoService
			.RunNetworkGitWithRetryAsync(clonePath, ["pull", "--ff-only"], null, cancellationToken)
			.ConfigureAwait(false);
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
