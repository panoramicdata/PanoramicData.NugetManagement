using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Maps what is selected to which repositories the toolbar acts on.
/// </summary>
/// <remarks>
/// The toolbar was written around one selected repository. The organisation, Issues and Repositories
/// nodes stand for many, so there a button acts on the repositories <em>ticked in the table</em>: no
/// ticks means nothing, because one stray click on a forty-repository estate must not touch it. Fix
/// already establishes the rule this follows: a button acts on everything beneath the selected node.
/// This says which repositories that is, and which steps are willing to be run that way.
/// <para>
/// A separate type rather than a method on the page, for the reason <see cref="FixScope"/> gives:
/// the page cannot be unit tested, and this mapping is the part worth being sure of.
/// </para>
/// </remarks>
public static class ToolbarScope
{
	/// <summary>
	/// A bulk step that would act on more repositories than this asks first. One threshold for every
	/// guarded step, so the guards cannot disagree about what counts as "many".
	/// </summary>
	public const int ConfirmAboveRepositoryCount = 1;

	/// <summary>
	/// Whether this selection acts on many repositories rather than on the one that is selected.
	/// </summary>
	/// <param name="view">The selected node's view.</param>
	public static bool IsEstateWide(NavView view)
		=> view is NavView.Repositories or NavView.Issues or NavView.Organisation;

	/// <summary>
	/// Whether a step may be run across many repositories at once.
	/// </summary>
	/// <remarks>
	/// Everything but Publish. Publishing pushes packages to nuget.org, which no revert undoes and
	/// which burns version numbers that can never be reused — the one step here whose mistake cannot
	/// be taken back, so it stays a decision made one repository at a time.
	/// </remarks>
	/// <param name="step">The step in question.</param>
	public static bool AllowsEstateWide(WorkflowStep step) => step is not WorkflowStep.Publish;

	/// <summary>
	/// Whether a step with nothing ticked acts on the whole organisation instead of on nothing.
	/// </summary>
	/// <remarks>
	/// Only re-assessing. It reads and writes nothing in any working tree, so the worst a stray click
	/// can do is spend an API budget the user is warned about first; every step that changes a clone or
	/// a remote needs an explicit choice of repositories.
	/// </remarks>
	/// <param name="step">The step in question.</param>
	public static bool FallsBackToWholeOrganisation(WorkflowStep step) => step is WorkflowStep.Reassess;

	/// <summary>
	/// Whether a step works on a clone on disk, and so skips a repository that has none.
	/// </summary>
	/// <remarks>
	/// Re-assessing does not: it reads the repository from GitHub, so it can describe one that was
	/// never cloned.
	/// </remarks>
	/// <param name="step">The step in question.</param>
	public static bool RequiresClone(WorkflowStep step) => step is not WorkflowStep.Reassess;

	/// <summary>
	/// The repositories a press of <paramref name="step"/> should act on.
	/// </summary>
	/// <param name="rows">The repositories in view.</param>
	/// <param name="step">The step being run.</param>
	/// <param name="isExcluded">Whether a repository has been excluded from governance.</param>
	/// <param name="selection">The repositories ticked in the table.</param>
	/// <remarks>
	/// Ticked, governed and not excluded — and cloned, for a step that works on a clone. An excluded
	/// repository takes no part in any figure or action, and a repository with no clone has nothing on
	/// disk to build, test, fix or push. The count that comes back can be smaller than the number
	/// ticked, which is why <see cref="DescribeSelection"/> exists: quietly acting on twelve of
	/// forty-seven is how a bulk action lies about what it did.
	/// </remarks>
	public static List<RepositoryDashboardRow> Targets(
		IEnumerable<RepositoryDashboardRow> rows,
		WorkflowStep step,
		Func<string, bool> isExcluded,
		RepositorySelection selection)
	{
		if (!AllowsEstateWide(step))
		{
			return [];
		}

		var eligible = rows.Where(row =>
			row.IsGoverned
			&& !isExcluded(row.RepositoryFullName)
			&& (!RequiresClone(step) || row.IsClonedLocally));

		if (selection.Count == 0)
		{
			return FallsBackToWholeOrganisation(step) ? [.. eligible] : [];
		}

		return [.. eligible.Where(row => selection.Contains(row.RepositoryFullName))];
	}

	/// <summary>
	/// What a press is about to do, in a sentence, including what it is leaving out.
	/// </summary>
	/// <param name="stepName">The step's label, as the button shows it.</param>
	/// <param name="targetCount">How many repositories it will act on.</param>
	/// <param name="selectedCount">How many repositories are ticked.</param>
	public static string DescribeSelection(string stepName, int targetCount, int selectedCount)
	{
		if (selectedCount == 0)
		{
			return $"{stepName}: tick repositories to act on them.";
		}

		if (targetCount == 0)
		{
			return $"{stepName} has nothing to act on: none of the {selectedCount} ticked "
				+ $"{Repositories(selectedCount)} can be acted on (excluded, not governed, or not cloned locally).";
		}

		var skipped = selectedCount - targetCount;
		var sentence = $"{stepName} will run on {targetCount} {Repositories(targetCount)}.";

		return skipped > 0
			? $"{sentence} {skipped} ticked {Repositories(skipped)} skipped: excluded, or not cloned locally."
			: sentence;
	}

	/// <summary>
	/// Whether pressing <paramref name="step"/> should ask first.
	/// </summary>
	/// <param name="step">The step being run.</param>
	/// <param name="targetCount">How many repositories it would act on.</param>
	/// <remarks>
	/// Only Commit &amp; Push: it changes remote state. Sync, Build and Test change nothing a revert
	/// cannot undo, Fix edits local clones, and Re-assess is read-only.
	/// </remarks>
	public static bool RequiresConfirmation(WorkflowStep step, int targetCount)
		=> step is WorkflowStep.CommitAndPush && targetCount > ConfirmAboveRepositoryCount;

	/// <summary>
	/// Whether pressing Fix with AI should ask first.
	/// </summary>
	/// <remarks>
	/// Fix with AI is not a <see cref="WorkflowStep"/>, so it has its own method over the same
	/// threshold. It starts model sessions, one per repository, which is worth saying before it does.
	/// </remarks>
	/// <param name="targetCount">How many repositories it would start work in.</param>
	public static bool RequiresAiFixConfirmation(int targetCount) => targetCount > ConfirmAboveRepositoryCount;

	/// <summary>
	/// The question put to the user before a guarded press, naming how many repositories and which.
	/// </summary>
	/// <param name="stepName">What is about to happen, as a verb phrase: "Commit &amp; push".</param>
	/// <param name="repositoryNames">The repositories' short names.</param>
	public static string ConfirmationMessage(string stepName, IReadOnlyList<string> repositoryNames)
	{
		const int shown = 8;

		var listed = string.Join(", ", repositoryNames.Take(shown));
		var more = repositoryNames.Count - shown;

		return more > 0
			? $"{stepName} {repositoryNames.Count} repositories? {listed} and {more} more."
			: $"{stepName} {repositoryNames.Count} {Repositories(repositoryNames.Count)}? {listed}.";
	}

	private static string Repositories(int count) => count == 1 ? "repository" : "repositories";
}
