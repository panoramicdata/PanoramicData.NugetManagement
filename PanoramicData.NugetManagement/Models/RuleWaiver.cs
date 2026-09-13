namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// A deliberate decision not to hold one repository to one rule.
/// </summary>
/// <remarks>
/// A waiver is not a way to hide a failure: the rule is still evaluated, and the result still
/// appears, marked as waived and carrying the <see cref="Reason"/>. What a waiver changes is that
/// the failure stops counting against the repository. The reason is mandatory precisely because a
/// waiver outlives the conversation that produced it — whoever finds it in six months needs to be
/// able to judge whether it still holds.
/// </remarks>
public sealed class RuleWaiver
{
	/// <summary>
	/// The rule this waives, e.g. "HTTP-01".
	/// </summary>
	public string RuleId { get; set; } = string.Empty;

	/// <summary>
	/// Why this repository is not held to the rule. Mandatory.
	/// </summary>
	public string Reason { get; set; } = string.Empty;

	/// <summary>
	/// Who recorded the waiver, when known.
	/// </summary>
	public string? WaivedBy { get; set; }

	/// <summary>
	/// When the waiver was recorded, when known.
	/// </summary>
	public DateTimeOffset? WaivedOnUtc { get; set; }
}
