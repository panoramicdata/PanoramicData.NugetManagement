using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Decides which toolbar steps a queued piece of work puts out of reach.
/// </summary>
/// <remarks>
/// Queueing a step means the repository is about to change underneath every step that follows it: a
/// Fix that has not run yet makes the current Build result, test result and publishability guesses
/// rather than facts. So a queued step closes itself and everything downstream, and leaves everything
/// upstream open — Sync and Re-assess can still be queued behind a Fix, which is how a user lines up
/// a whole pass in one go.
/// </remarks>
public static class WorkflowGate
{
	/// <summary>
	/// The earliest workflow step queued or running for <paramref name="repositoryFullName"/>, or null
	/// when nothing is outstanding for it.
	/// </summary>
	/// <param name="items">The work queue, in any order.</param>
	/// <param name="repositoryFullName">
	/// The repository the toolbar is showing, or null when none is selected — work is only ever gated
	/// against a specific repository.
	/// </param>
	public static WorkflowStep? FirstBlockedStep(
		IEnumerable<WorkItem> items,
		string? repositoryFullName)
	{
		if (string.IsNullOrEmpty(repositoryFullName))
		{
			return null;
		}

		WorkflowStep? earliest = null;

		foreach (var item in items)
		{
			if (item.Step is not { } step
				|| !string.Equals(item.RepositoryFullName, repositoryFullName, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var rank = Rank(step);
			if (earliest is null || rank < Rank(earliest.Value))
			{
				earliest = step;
			}
		}

		return earliest;
	}

	/// <summary>
	/// Whether <paramref name="step"/> should be greyed out given the earliest queued step, as returned
	/// by <see cref="FirstBlockedStep"/>.
	/// </summary>
	public static bool IsBlocked(WorkflowStep step, WorkflowStep? firstBlockedStep)
		=> firstBlockedStep is { } blocked && Rank(step) >= Rank(blocked);

	/// <summary>
	/// A step's position in the workflow. Fix and Fix with AI share a position: they are two ways of
	/// doing the same step, so queueing either has to close both.
	/// </summary>
	private static int Rank(WorkflowStep step)
		=> (int)(step == WorkflowStep.FixWithAi ? WorkflowStep.Fix : step);
}
