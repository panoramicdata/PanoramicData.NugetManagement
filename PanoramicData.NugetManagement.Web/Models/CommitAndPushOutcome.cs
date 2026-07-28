namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// The outcome of a commit-and-push attempt.
/// </summary>
/// <remarks>
/// Distinguishes a guard refusing to push from the push itself going wrong, because the two deserve
/// different treatment: a refusal is a decision with a reason the user needs to read and act on, whereas
/// a git failure is already described by git's own output. The reason travels back rather than only
/// reaching the console, since the console can be collapsed — and for a single repository this is the
/// conclusion of the whole attempt.
/// </remarks>
public sealed record CommitAndPushOutcome
{
	/// <summary>Whether the changes were committed and pushed.</summary>
	public required bool Success { get; init; }

	/// <summary>
	/// Why a guard refused, or null when nothing refused — including when the push simply failed.
	/// </summary>
	public string? RefusalReason { get; init; }

	/// <summary>Whether this attempt was stopped by a guard rather than by a failure.</summary>
	public bool WasRefused => RefusalReason is not null;

	/// <summary>The push happened.</summary>
	public static CommitAndPushOutcome Pushed { get; } = new() { Success = true };

	/// <summary>The push was attempted and failed; git will have said why.</summary>
	public static CommitAndPushOutcome Failed { get; } = new() { Success = false };

	/// <summary>A guard stopped the push before anything was committed.</summary>
	public static CommitAndPushOutcome Refused(string reason) => new() { Success = false, RefusalReason = reason };
}
