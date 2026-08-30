using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Where a queued unit of work has got to.
/// </summary>
public enum WorkItemState
{
	/// <summary>Waiting for its turn. Nothing has happened yet, so it can be removed freely.</summary>
	Pending,

	/// <summary>Executing. Exactly one item per lane is in this state at a time.</summary>
	Running,

	/// <summary>Stop has been asked for; the item is unwinding and reverting anything half-applied.</summary>
	Cancelling,

	/// <summary>Finished without error.</summary>
	Completed,

	/// <summary>Finished by throwing.</summary>
	Failed,

	/// <summary>Stopped before it finished. Anything it had half-applied has been reverted.</summary>
	Cancelled
}

/// <summary>
/// One unit of queued work: a single action the user asked for, acting on one repository or on one
/// organisation.
/// </summary>
/// <remarks>
/// A class rather than a record because <see cref="State"/>, <see cref="Progress"/> and
/// <see cref="GeneratedPrompt"/> change while the UI holds a reference to the item.
/// <para>
/// The item no longer carries a delegate or an owning component. Work is named
/// (<see cref="Descriptor"/>) and executed by <see cref="WorkRunnerService"/>, so it belongs to the
/// application rather than to the browser tab that asked for it.
/// </para>
/// </remarks>
public sealed class WorkItem
{
	/// <summary>Identifies the item within the queue.</summary>
	public required string Id { get; init; }

	/// <summary>What the user sees in the tree, e.g. "Fix panoramicdata/Athonet.Api".</summary>
	public required string Title { get; init; }

	/// <summary>What this item will do.</summary>
	public required WorkDescriptor Descriptor { get; init; }

	/// <summary>
	/// Identifies work that would repeat what is already queued in this lane. A second enqueue with
	/// the same key is folded into the pending item rather than queued again.
	/// </summary>
	public required string DedupKey { get; init; }

	/// <summary>
	/// The workflow step this work performs, or null for work that is not a step on the toolbar.
	/// Queueing a step closes it and everything downstream — see <see cref="WorkflowGate"/>.
	/// </summary>
	public WorkflowStep? Step { get; init; }

	/// <summary>
	/// The console this item's output belongs to, recorded when it was queued rather than read when it
	/// runs: the lane may not reach it for minutes, by which time the selection has moved and the
	/// output would land in an unrelated console.
	/// </summary>
	public string? ConsoleNodeKey { get; init; }

	/// <summary>The lane this item runs on.</summary>
	public string LaneKey => Descriptor.LaneKey;

	/// <summary>The organisation this work is scoped to, or null when it spans every organisation.</summary>
	public string? Organization => Descriptor.Organization;

	/// <summary>
	/// The repository this work acts on, or null for organisation-scoped work. What the toolbar gates
	/// against: work on one repository never closes another repository's buttons.
	/// </summary>
	public string? RepositoryFullName => Descriptor.RepositoryFullName;

	/// <summary>Where the item has got to.</summary>
	public WorkItemState State { get; set; } = WorkItemState.Pending;

	/// <summary>Progress within the item, e.g. "repo 8 of 47". Null until the work reports some.</summary>
	public string? Progress { get; set; }

	/// <summary>
	/// The AI prompt the work produced for issues it could not fix, or null when it produced none.
	/// </summary>
	/// <remarks>
	/// Held rather than pushed. The work used to write this straight to the browser clipboard and open
	/// an IDE, which cannot be done from a runner with no browser attached — and twenty lanes finishing
	/// together would have raced twenty of each. The user claims it from the prompt UI instead.
	/// </remarks>
	public string? GeneratedPrompt { get; set; }

	/// <summary>
	/// Whether this item was running when the process last stopped. Such an item is restored as
	/// pending, and its working tree is cleaned before it is run again.
	/// </summary>
	public bool WasInterrupted { get; init; }

	/// <summary>
	/// Whether the work reported the thing it was asked to do as having succeeded — as distinct from
	/// whether it ran without throwing.
	/// </summary>
	/// <remarks>
	/// A failed build or a refused push is not an exception: the executor logs it and returns. Deducing
	/// the outcome from <see cref="State"/> therefore reads a failed build as a success, which is how the
	/// step badges came to show a green tick on a red build. Executors state the outcome instead.
	/// Null when the work does not report one.
	/// </remarks>
	public bool? Succeeded { get; set; }
}
