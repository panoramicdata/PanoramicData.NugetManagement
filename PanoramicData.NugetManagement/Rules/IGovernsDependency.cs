using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Implemented by a rule that enforces a <em>minimum version</em> of a named dependency.
/// </summary>
/// <remarks>
/// Dependabot pull request triage uses this to decide whether a still-valid pull request is one the
/// existing remediations already cover. Coverage means a failing rule that governs the dependency
/// exists and has a remediation; a pull request nothing governs raises an issue instead.
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
}
