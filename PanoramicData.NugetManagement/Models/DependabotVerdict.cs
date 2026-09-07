namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// What triage concluded about one open Dependabot pull request.
/// </summary>
public enum DependabotVerdict
{
	/// <summary>
	/// Not a single-dependency version bump raised by Dependabot, or a title the parser does not
	/// recognise. Left strictly alone: not closed, and no issue raised.
	/// </summary>
	Unrecognised,

	/// <summary>
	/// The repository already declares a version at or above the target, so merging would change
	/// nothing. Closed, with a comment saying why.
	/// </summary>
	AlreadySatisfied,

	/// <summary>
	/// Still worth doing, and a failing rule with a remediation would do it. The existing fix
	/// pipeline handles it — and the next triage pass then finds it already satisfied.
	/// </summary>
	ValidCovered,

	/// <summary>
	/// Still worth doing, and nothing we have can do it automatically. Raises an issue against this
	/// application's own repository, so the missing remediation becomes visible work.
	/// </summary>
	ValidUncovered,

	/// <summary>
	/// Still worth doing, nothing is failing for it, and the pull request has been open long enough
	/// that no grace period is still protecting anything — so the bumps it proposes are written to the
	/// local clone and the pull request is closed.
	/// </summary>
	/// <remarks>
	/// Named for the state triage found rather than the act the runner performs, as
	/// <see cref="ValidCovered"/> is. Triage decides; the runner adopts, and only if the write applies
	/// something.
	/// <para>
	/// Appended last when it was added, and nothing may be inserted above it.
	/// <c>RepositoryIssue.TriageVerdict</c> is persisted to the row cache as a JSON number, so a member
	/// added anywhere but the end silently rewrites the meaning of every verdict already cached.
	/// </para>
	/// </remarks>
	Adoptable,

	/// <summary>
	/// The repository does not reference the dependency anywhere it could be declaring one, so the
	/// pull request proposes moving something that is not here. Closed, with a comment saying so.
	/// </summary>
	/// <remarks>
	/// Distinct from <see cref="ValidUncovered"/> with a rule-set gap, which it used to be reported as.
	/// A gap means a fix is missing and somebody should write one; this means there is nothing to fix,
	/// and raising an issue for it asks for a remediation covering a dependency the estate has already
	/// dropped.
	/// <para>
	/// The only verdict that closes a pull request on the strength of not finding something, which is
	/// why <see cref="Services.DependencyMentionScanner"/> looks as widely as it does, and why one
	/// still-referenced bump anywhere in a group is enough to keep the pull request open.
	/// </para>
	/// <para>
	/// Appended last, and it must stay last, for the cache reason given on <see cref="Adoptable"/>.
	/// </para>
	/// </remarks>
	Obsolete
}
