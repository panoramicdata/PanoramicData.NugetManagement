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
	/// Still worth doing, and nothing we have can ever do it automatically: no rule governs the
	/// dependency, or the rule that claims it never reads where it is declared. Raises an issue against
	/// this application's own repository, so the missing remediation becomes visible work.
	/// </summary>
	/// <remarks>
	/// A gap, and only a gap. A dependency whose rule is merely passing today is <see cref="NotYetDue"/>
	/// instead: both mean "no failure is queued for this", but only this one is somebody's work.
	/// </remarks>
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
	Obsolete,

	/// <summary>
	/// Still worth doing, governed by a rule that has a remediation, but no failure of that rule is
	/// queued to move it today — typically because the release is still inside the rule's grace period.
	/// Adopted when Fix is pressed; no issue is raised.
	/// </summary>
	/// <remarks>
	/// Informational, not a verdict that declines to act. Triage runs only because somebody pressed
	/// Fix, and a grace period exists so that nothing churns automatically on a release published this
	/// morning — not to refuse a bump asked for by name. So this reports <em>why</em> a pull request was
	/// sitting there, while Fix goes ahead and adopts it exactly as it would an <see cref="Adoptable"/>
	/// one.
	/// <para>
	/// What separates the two is only which sentence the row shows:
	/// <see cref="Adoptable"/> means the <c>AdoptAfter</c> backstop has expired, this means it has not.
	/// Neither changes what Fix does.
	/// </para>
	/// <para>
	/// Split out of <see cref="ValidUncovered"/>, which had carried both meanings and rendered both as
	/// "No auto-fix" — a label that read as "nobody will ever fix this" for a pull request a fix was
	/// about to be applied to.
	/// </para>
	/// <para>
	/// Appended last, and it must stay last, for the cache reason given on <see cref="Adoptable"/>.
	/// Rows cached under the old meaning re-stamp themselves on the next pass.
	/// </para>
	/// </remarks>
	NotYetDue
}
