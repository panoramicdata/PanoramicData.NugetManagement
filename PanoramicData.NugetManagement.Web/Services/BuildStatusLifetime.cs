using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Decides how long a remembered build result is worth believing.
/// </summary>
/// <remarks>
/// A green build badge claims that this exact working tree built. Anything that rewrites a file in
/// that tree makes the claim false, and a stale green is worse than no badge at all — it is read at
/// exactly the moment it misleads, straight after a bulk remediation. So the result is thrown away
/// rather than aged: back to not-known, until someone builds again.
/// <para>
/// A separate type rather than a switch inside the executor, for the reason <see cref="FixScope"/>
/// gives: the mapping is the part worth being sure of, and this can be unit tested.
/// </para>
/// </remarks>
public static class BuildStatusLifetime
{
	/// <summary>
	/// Kinds that rewrite files in the working tree, so a build result taken before one ran no longer
	/// describes what is on disk.
	/// </summary>
	private static readonly HashSet<WorkKind> _invalidating =
	[
		WorkKind.FixAll,
		WorkKind.FixCategory,
		WorkKind.FixRule,

		// A model rewrote files in that tree, and it is the kind of writer whose work most deserves
		// rebuilding before anyone believes a green badge over it. Invalidating even on failure is right:
		// a failed attempt reverts the clone, and a revert changes the tree as surely as a fix does.
		WorkKind.FixWithAiRule,
		WorkKind.GitSync,
		WorkKind.Clone
	];

	/// <summary>
	/// Kinds that leave the working tree's contents as they found them.
	/// </summary>
	/// <remarks>
	/// Commit &amp; push is here deliberately. Committing changes no file, and while its regression
	/// guard can revert one, that outcome already announces itself through
	/// <see cref="NavItem.GuardStateNeedingAttention"/> — greying every repository in a successful
	/// sweep to cover the rare case would empty the board each time it was used.
	/// </remarks>
	private static readonly HashSet<WorkKind> _preserving =
	[
		WorkKind.Build,
		WorkKind.Test,
		WorkKind.Reassess,
		WorkKind.TriageDependabot,
		WorkKind.CommitAndPush,
		WorkKind.Publish,
		WorkKind.RediscoverOrganization,
		WorkKind.DiscoverReassessTargets,
		WorkKind.DiscoverCloneTargets,
		WorkKind.RefreshAll
	];

	/// <summary>
	/// Whether finishing this kind of work means the repository's build result can no longer be
	/// believed.
	/// </summary>
	/// <param name="kind">The work that ran.</param>
	public static bool Invalidates(WorkKind kind) => _invalidating.Contains(kind);

	/// <summary>
	/// Whether this kind has been considered at all.
	/// </summary>
	/// <remarks>
	/// Exposed so a test can hold the two lists to <see cref="WorkKind"/> in full. A kind added to the
	/// enum and to neither list would default to preserving the result, which is the answer that can
	/// be silently wrong.
	/// </remarks>
	/// <param name="kind">The kind to check.</param>
	public static bool IsKnown(WorkKind kind) => _invalidating.Contains(kind) || _preserving.Contains(kind);
}
