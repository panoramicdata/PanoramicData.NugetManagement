using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Maps what is selected to which repositories the toolbar acts on.
/// </summary>
/// <remarks>
/// The toolbar was written around one selected repository, so selecting the Repositories branch — a
/// container over the whole estate — offered nothing at all. Fix already establishes the rule this
/// follows: a button acts on everything beneath the selected node. This says which repositories that
/// is, and which steps are willing to be run that way.
/// <para>
/// A separate type rather than a method on the page, for the reason <see cref="FixScope"/> gives:
/// the page cannot be unit tested, and this mapping is the part worth being sure of.
/// </para>
/// </remarks>
public static class ToolbarScope
{
	/// <summary>
	/// Whether this selection acts on many repositories rather than on the one that is selected.
	/// </summary>
	/// <param name="view">The selected node's view.</param>
	public static bool IsEstateWide(NavView view) => view is NavView.Repositories;

	/// <summary>
	/// Whether a step may be run across a whole estate at once.
	/// </summary>
	/// <remarks>
	/// Everything but Publish. Publishing pushes packages to nuget.org, which no revert undoes and
	/// which burns version numbers that can never be reused — the one step here whose mistake cannot
	/// be taken back, so it stays a decision made one repository at a time.
	/// </remarks>
	/// <param name="step">The step in question.</param>
	public static bool AllowsEstateWide(WorkflowStep step) => step is not WorkflowStep.Publish;

	/// <summary>
	/// The repositories an estate-wide press of <paramref name="step"/> should act on.
	/// </summary>
	/// <param name="rows">The repositories in view.</param>
	/// <param name="step">The step being run.</param>
	/// <param name="isExcluded">Whether a repository has been excluded from governance.</param>
	/// <remarks>
	/// Governed, not excluded, and cloned. An excluded repository takes no part in any figure or
	/// action, and a repository with no clone has nothing on disk to build, test, fix or push. The
	/// count that comes back is smaller than the estate, which is why <see cref="Describe"/> exists:
	/// quietly acting on twelve of forty-seven is how a bulk action lies about what it did.
	/// </remarks>
	public static List<RepositoryDashboardRow> Targets(
		IEnumerable<RepositoryDashboardRow> rows,
		WorkflowStep step,
		Func<string, bool> isExcluded)
		=> AllowsEstateWide(step)
			? [.. rows.Where(row =>
				row.IsGoverned
				&& row.IsClonedLocally
				&& !isExcluded(row.RepositoryFullName))]
			: [];

	/// <summary>
	/// What an estate-wide press is about to do, in a sentence, including what it is leaving out.
	/// </summary>
	/// <param name="stepName">The step's label, as the button shows it.</param>
	/// <param name="targetCount">How many repositories it will act on.</param>
	/// <param name="candidateCount">How many were in view before the skips.</param>
	public static string Describe(string stepName, int targetCount, int candidateCount)
	{
		if (targetCount == 0)
		{
			return $"{stepName} has nothing to act on here.";
		}

		var skipped = candidateCount - targetCount;
		var sentence = $"{stepName} will run on {targetCount} {Repositories(targetCount)}.";

		return skipped > 0
			? $"{sentence} {skipped} {Repositories(skipped)} skipped: excluded, or not cloned locally."
			: sentence;
	}

	private static string Repositories(int count) => count == 1 ? "repository" : "repositories";
}
