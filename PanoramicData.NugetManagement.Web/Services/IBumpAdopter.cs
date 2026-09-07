using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Writes an adoption plan to a repository's local clone.
/// </summary>
/// <remarks>
/// A port so <see cref="DependabotTriageRunner"/> can stay what its documentation claims: a component
/// whose writes are all to GitHub, testable with no clone, no working tree and no network. The runner
/// decides whether to close a pull request from what this reports, and nothing else.
/// </remarks>
public interface IBumpAdopter
{
	/// <summary>
	/// Writes the plan, and reports the files it changed.
	/// </summary>
	/// <param name="plan">What to write.</param>
	/// <param name="onOutput">Where progress is announced.</param>
	/// <returns>The files changed. Empty means nothing was written.</returns>
	IReadOnlyList<string> Adopt(DependabotAdoptionPlan plan, Action<string> onOutput);
}
