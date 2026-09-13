namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// A waiver as it was applied to one rule result, together with the verdict the rule would have
/// given without it.
/// </summary>
/// <remarks>
/// Keeping the underlying verdict is the whole reason waived rules are still evaluated. A waiver is
/// agreed against a problem, and problems get fixed; without the verdict there is no way to notice
/// that the rule now passes on its own and the waiver is just clutter nobody dares delete.
/// </remarks>
public sealed class AppliedWaiver
{
	/// <summary>
	/// The recorded waiver — its reason, and who agreed it.
	/// </summary>
	public required RuleWaiver Waiver { get; init; }

	/// <summary>
	/// Whether the rule passed anyway, ignoring the waiver.
	/// </summary>
	public required bool UnderlyingPassed { get; init; }

	/// <summary>
	/// What the rule said before the waiver was applied.
	/// </summary>
	public required string UnderlyingMessage { get; init; }

	/// <summary>
	/// Whether the waiver is no longer needed, because the rule passes without it.
	/// </summary>
	public bool IsStale => UnderlyingPassed;
}
