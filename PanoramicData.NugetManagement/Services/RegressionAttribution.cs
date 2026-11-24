namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Pure logic for deciding whether a build regression is attributable to this tool's own commits.
/// </summary>
public static class RegressionAttribution
{
	/// <summary>
	/// Determines whether a commit is one this tool produced when applying a governance remediation.
	/// The <see cref="GovernanceCommitMessage.TrailerKey"/> trailer decides when present; the subject is
	/// the fallback for commits made before it existed. Deliberately does NOT match our revert commits,
	/// so an auto-rollback is never itself rolled back.
	/// </summary>
	public static bool IsGovernanceCommit(string subject) => IsGovernanceCommit(subject, null);

	/// <inheritdoc cref="IsGovernanceCommit(string)"/>
	/// <param name="subject">The commit subject.</param>
	/// <param name="remediationTrailer">The value of the governance trailer, or null when the commit has none.</param>
	public static bool IsGovernanceCommit(string subject, string? remediationTrailer)
		=> !string.IsNullOrWhiteSpace(remediationTrailer)
			|| GovernanceCommitMessage.IsRemediationSubject(subject)
			|| (!string.IsNullOrWhiteSpace(subject)
				&& subject.StartsWith("chore: apply", StringComparison.OrdinalIgnoreCase)
				&& subject.Contains("governance remediation", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Bisects our consecutive commits to the first one at which the build fails.
	/// </summary>
	/// <param name="ourCommitsNewestFirst">Our run of commits at HEAD, newest first. HEAD is known to fail.</param>
	/// <param name="buildsAtCommit">Whether the build succeeds at the given commit hash.</param>
	/// <returns>The earliest commit of the run that does not build.</returns>
	public static async Task<GitCommit> FindFirstBreakingCommitAsync(
		IReadOnlyList<GitCommit> ourCommitsNewestFirst,
		Func<string, Task<bool>> buildsAtCommit)
	{
		var oldestFirst = ourCommitsNewestFirst.Reverse().ToList();
		var low = 0;
		var high = oldestFirst.Count - 1;

		while (low < high)
		{
			var middle = (low + high) / 2;
			if (await buildsAtCommit(oldestFirst[middle].Hash).ConfigureAwait(false))
			{
				low = middle + 1;
			}
			else
			{
				high = middle;
			}
		}

		return oldestFirst[low];
	}

	/// <summary>
	/// Given the recent commits (newest first), identifies the run of consecutive commits at HEAD
	/// that this tool authored.
	/// </summary>
	/// <param name="commitsNewestFirst">Recent commits as (hash, subject), newest first.</param>
	/// <returns>
	/// The number of our consecutive commits at HEAD, and a git ref for the last known-good commit
	/// (the parent of the earliest of those). <c>OurCount</c> is 0 and <c>LastGoodRef</c> is null when
	/// the tip commit was not authored by this tool (so the breakage is not ours to roll back).
	/// </returns>
	public static (int OurCount, string? LastGoodRef) Identify(
		IReadOnlyList<GitCommit> commitsNewestFirst)
	{
		var ourCount = 0;
		foreach (var commit in commitsNewestFirst)
		{
			if (!IsGovernanceCommit(commit.Subject, commit.RemediationTrailer))
			{
				break;
			}

			ourCount++;
		}

		if (ourCount == 0)
		{
			return (0, null);
		}

		// The earliest of our consecutive commits is the last one counted; its parent is last-good.
		var earliestOursHash = commitsNewestFirst[ourCount - 1].Hash;
		return (ourCount, $"{earliestOursHash}~1");
	}
}
