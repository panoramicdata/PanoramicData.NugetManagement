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
	/// Appended last, and it must stay last. <c>RepositoryIssue.TriageVerdict</c> is persisted to the
	/// row cache as a JSON number, so inserting a member above this one silently rewrites the meaning
	/// of every verdict already cached.
	/// </para>
	/// </remarks>
	Adoptable
}
