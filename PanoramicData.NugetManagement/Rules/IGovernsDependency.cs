using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Implemented by a rule that enforces a <em>minimum version</em> of a named dependency.
/// </summary>
/// <remarks>
/// Dependabot pull request triage uses this to decide whether a still-valid pull request is one the
/// existing remediations already cover. Coverage means a failing rule that governs the dependency
/// exists, has a remediation, and will move <em>this</em> dependency; a pull request nothing governs
/// raises an issue instead.
/// <para>
/// Opt-in, and narrowly so. A rule that checks a dependency merely <em>exists</em> — as
/// <c>CodeQlWorkflowRule</c> checks for a <c>github/codeql-action</c> workflow — must not implement
/// this: it cannot move a version, so it cannot cover a version bump, and claiming otherwise would
/// swallow exactly the gap triage exists to surface.
/// </para>
/// </remarks>
public interface IGovernsDependency
{
	/// <summary>
	/// Whether this rule enforces a minimum version of the given dependency.
	/// </summary>
	/// <param name="dependency">The dependency a Dependabot pull request would move.</param>
	bool Governs(DependencyRef dependency);

	/// <summary>
	/// Whether this particular failure will move this particular dependency.
	/// </summary>
	/// <param name="failure">A failing result of this rule.</param>
	/// <param name="dependency">The dependency a Dependabot pull request would move.</param>
	/// <remarks>
	/// <see cref="Governs"/> answers what a rule is <em>about</em>; this answers what one failure of it
	/// will actually do. The two differ whenever a rule claims a class of dependency but fixes only
	/// what it found wrong: CI-12 claims every unclaimed action and moves the ones it found behind, and
	/// the package rules claim every NuGet package and move the ones they named.
	/// <para>
	/// Deliberately not a default implementation. Answering "yes, always" is right only for a rule that
	/// governs a single named dependency, and a rule that inherited that answer by saying nothing would
	/// report pull requests as covered by a fix that never touches them — they would then sit open
	/// indefinitely, waiting, with no gap issue raised because they look handled. Silence is the
	/// dangerous direction here, so every implementer has to answer for itself.
	/// </para>
	/// </remarks>
	bool WillMove(RuleResult failure, DependencyRef dependency);
}
