namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// The outcome of a Commit &amp; push, which delivers a clone's changes to main through a pull request.
/// </summary>
/// <remarks>
/// Distinguishes a guard refusing from the delivery itself going wrong, because the two deserve
/// different treatment: a refusal is a decision with a reason the user needs to read and act on, whereas
/// a git failure is already described by git's own output. The reason travels back rather than only
/// reaching the console, since the console can be collapsed, and for a single repository this is the
/// conclusion of the whole attempt.
/// </remarks>
public sealed record CommitAndPushOutcome
{
	/// <summary>Whether the changes reached a pull request, or there was nothing to deliver.</summary>
	public required bool Success { get; init; }

	/// <summary>
	/// Why a guard refused, or null when nothing refused, including when the delivery simply failed.
	/// </summary>
	public string? RefusalReason { get; init; }

	/// <summary>Why the delivery failed, when it is not already in git's output.</summary>
	public string? FailureReason { get; init; }

	/// <summary>Whether this attempt was stopped by a guard rather than by a failure.</summary>
	public bool WasRefused => RefusalReason is not null;

	/// <summary>Whether the guard also took the uncommitted changes out of the clone.</summary>
	public bool ChangesReverted { get; init; }

	/// <summary>Whether the clone had nothing for main, so no pull request was opened.</summary>
	public bool NothingToDeliver { get; init; }

	/// <summary>The pull request the changes went to, or null when none was opened.</summary>
	public PullRequestDelivery? PullRequest { get; init; }

	/// <summary>The changes are on a pull request into main.</summary>
	/// <param name="pullRequest">The pull request.</param>
	public static CommitAndPushOutcome Delivered(PullRequestDelivery pullRequest) => new() { Success = true, PullRequest = pullRequest };

	/// <summary>There was nothing to deliver.</summary>
	public static CommitAndPushOutcome NothingDelivered { get; } = new() { Success = true, NothingToDeliver = true };

	/// <summary>The delivery was attempted and failed; git will have said why.</summary>
	public static CommitAndPushOutcome Failed { get; } = new() { Success = false };

	/// <summary>The delivery failed for a reason git did not report.</summary>
	/// <param name="reason">What went wrong and what to do about it.</param>
	public static CommitAndPushOutcome FailedBecause(string reason) => new() { Success = false, FailureReason = reason };

	/// <summary>A guard stopped the delivery before anything was committed.</summary>
	/// <param name="reason">Why.</param>
	public static CommitAndPushOutcome Refused(string reason) => new() { Success = false, RefusalReason = reason };
}
