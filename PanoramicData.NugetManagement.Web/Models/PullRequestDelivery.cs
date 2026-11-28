namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Where a governance pull request was left.
/// </summary>
public enum PullRequestLanding
{
	/// <summary>GitHub will squash-merge it once the required checks pass.</summary>
	AutoMergeEnabled,

	/// <summary>It was squash-merged straight away, because GitHub had no required check to wait for.</summary>
	Merged,

	/// <summary>It is open and waits for a person.</summary>
	LeftOpen
}

/// <summary>
/// The pull request a Commit &amp; push opened.
/// </summary>
/// <param name="Branch">The governance branch it merges from.</param>
/// <param name="HeadSha">The branch's head commit.</param>
/// <param name="Number">The pull request number.</param>
/// <param name="Url">The pull request on GitHub.</param>
/// <param name="Landing">Where it was left.</param>
/// <param name="Note">Why it was left open, when it was; otherwise null.</param>
public sealed record PullRequestDelivery(
	string Branch,
	string HeadSha,
	int Number,
	string Url,
	PullRequestLanding Landing,
	string? Note);
