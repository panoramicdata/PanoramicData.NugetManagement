namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Where a queued unit of work has got to.
/// </summary>
public enum WorkItemState
{
	/// <summary>Waiting for its turn. Nothing has happened yet, so it can be removed freely.</summary>
	Pending,

	/// <summary>Executing. Exactly one item is in this state at a time, application-wide.</summary>
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
/// One unit of queued work: a single action the user asked for, however many repositories it touches.
/// </summary>
/// <remarks>
/// A class rather than a record because <see cref="State"/> and <see cref="Progress"/> change while
/// the UI holds a reference to the item.
/// </remarks>
public sealed class WorkItem
{
	/// <summary>Identifies the item within the queue.</summary>
	public required string Id { get; init; }

	/// <summary>What the user sees in the queue, e.g. "Apply TST-06 &amp; push — 12 repos".</summary>
	public required string Title { get; init; }

	/// <summary>The organisation this work is scoped to, or null when it spans every organisation.</summary>
	public string? Organization { get; init; }

	/// <summary>
	/// Identifies work that would repeat what is already queued. A second enqueue with the same key
	/// is folded into the pending item rather than queued again.
	/// </summary>
	public required string DedupKey { get; init; }

	/// <summary>
	/// The component that enqueued the item and will execute it. Work belongs to the circuit that
	/// started it, so when that circuit goes away its work goes with it.
	/// </summary>
	public required object OwnerId { get; init; }

	/// <summary>
	/// The work itself. Reports progress lines through the supplied <see cref="IProgress{T}"/>, and
	/// must honour the token by reverting anything it has half-applied.
	/// </summary>
	public required Func<IProgress<string>, CancellationToken, Task> Run { get; init; }

	/// <summary>Where the item has got to.</summary>
	public WorkItemState State { get; set; } = WorkItemState.Pending;

	/// <summary>Progress within the item, e.g. "repo 8 of 47". Null until the work reports some.</summary>
	public string? Progress { get; set; }
}
