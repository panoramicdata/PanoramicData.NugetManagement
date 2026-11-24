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
	/// Given the recent commits (newest first), collects the ones this tool authored, wherever they sit
	/// in the range, so a human commit pushed on top does not make the breakage "not ours".
	/// </summary>
	/// <param name="commitsNewestFirst">Recent commits, newest first.</param>
	/// <returns>
	/// Our commits in the range (newest first), and a git ref for the last known-good commit. When none
	/// of the range is ours, <c>Ours</c> is empty and <c>LastGoodRef</c> is null.
	/// </returns>
	public static Attribution Identify(IReadOnlyList<GitCommit> commitsNewestFirst)
		=> Identify(commitsNewestFirst, null);

	/// <inheritdoc cref="Identify(IReadOnlyList{GitCommit})"/>
	/// <param name="commitsNewestFirst">Recent commits, newest first.</param>
	/// <param name="lastVerifiedHash">`n	/// The hash of the last commit known to build green, when there is one in memory. The range stops`n	/// there, and that commit is the control, so no control build is needed.`n	/// </param>
	public static Attribution Identify(IReadOnlyList<GitCommit> commitsNewestFirst, string? lastVerifiedHash)
	{
		var verifiedIndex = lastVerifiedHash is null
			? -1
			: IndexOfHash(commitsNewestFirst, lastVerifiedHash);
		var range = verifiedIndex < 0 ? commitsNewestFirst : [.. commitsNewestFirst.Take(verifiedIndex)];
		var ours = range.Where(commit => IsGovernanceCommit(commit.Subject, commit.RemediationTrailer)).ToList();

		if (ours.Count == 0)
		{
			return new Attribution([], null, false, false);
		}

		var newestIsHead = ours[0].Hash == commitsNewestFirst[0].Hash;
		return verifiedIndex >= 0
			? new Attribution(ours, commitsNewestFirst[verifiedIndex].Hash, true, newestIsHead)
			: new Attribution(ours, $"{ours[^1].Hash}~1", false, newestIsHead);
	}

	/// <summary>
	/// Bisects our commits to the first that fails to build, then confirms its parent builds. When a
	/// human commit sits between, the parent failing means the breakage is theirs, not ours.
	/// </summary>
	/// <param name="attribution">Our commits, as <c>Identify</c> returned them.</param>
	/// <param name="buildsAt">Whether the build succeeds at a git ref.</param>
	/// <returns>The first failing commit of ours, and whether the breakage is attributable to us.</returns>
	public static async Task<(GitCommit Culprit, bool IsOurs)> NameCulpritAsync(
		Attribution attribution,
		Func<string, Task<bool>> buildsAt)
	{
		// The bisect assumes the newest of ours fails. That holds at the tip; under a human commit it
		// has to be shown, or the human commit's breakage would be put down to us.
		if (!attribution.NewestIsHead && await buildsAt(attribution.Ours[0].Hash).ConfigureAwait(false))
		{
			return (attribution.Ours[0], false);
		}

		var culprit = await FindFirstBreakingCommitAsync(attribution.Ours, buildsAt).ConfigureAwait(false);

		// The earliest of ours has the control build (or the verified commit) as its parent.
		var isEarliest = culprit.Hash == attribution.Ours[^1].Hash;
		var parentBuilds = isEarliest || await buildsAt($"{culprit.Hash}~1").ConfigureAwait(false);

		return (culprit, parentBuilds);
	}
	private static int IndexOfHash(IReadOnlyList<GitCommit> commits, string hash)
	{
		for (var index = 0; index < commits.Count; index++)
		{
			if (string.Equals(commits[index].Hash, hash, StringComparison.OrdinalIgnoreCase))
			{
				return index;
			}
		}

		return -1;
	}
}

/// <summary>Which commits in a failing range are ours, and the commit to compare them against.</summary>
/// <param name="Ours">Our commits in the range, newest first.</param>
/// <param name="LastGoodRef">A git ref to build as the control, or null when none of the range is ours.</param>
/// <param name="LastGoodVerified">Whether <paramref name="LastGoodRef"/> is already known to build, so it needs no control build.</param>
/// <param name="NewestIsHead">Whether the newest of ours is the tip, which is known to fail; otherwise a human commit sits above it.</param>
public sealed record Attribution(IReadOnlyList<GitCommit> Ours, string? LastGoodRef, bool LastGoodVerified, bool NewestIsHead);