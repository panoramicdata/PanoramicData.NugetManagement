using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// The application-wide work queue: one unit of work runs at a time, whichever browser tab asked for
/// it, and what is running and what is waiting is visible to every tab.
/// </summary>
/// <remarks>
/// The queue coordinates but does not execute. Each item carries a delegate supplied by the component
/// that enqueued it, and that component runs it when the item reaches the head — see DESIGN.md §2.
/// Serialising here rather than in each component is what stops two tabs, or the dashboard and the
/// issues view, driving the same git working tree at once.
/// </remarks>
public sealed class WorkQueueService
{
	private readonly Lock _lock = new();
	private readonly List<WorkItem> _items = [];
	private WorkItem? _running;
	private CancellationTokenSource? _runningCts;
	private int _nextId;

	/// <summary>
	/// Raised whenever the queue changes: an item is added, starts, reports progress, or finishes.
	/// </summary>
	public event Action? Changed;

	/// <summary>
	/// The queue as it stands, in the order it will run: the running item first, then the pending
	/// ones. Finished items are removed, so this is only ever work outstanding.
	/// </summary>
	public IReadOnlyList<WorkItem> Items
	{
		get
		{
			lock (_lock)
			{
				return [.. _items];
			}
		}
	}

	/// <summary>The item currently executing, or null when the queue is idle.</summary>
	public WorkItem? Running
	{
		get
		{
			lock (_lock)
			{
				return _running;
			}
		}
	}

	/// <summary>
	/// Adds work to the queue, returning the queued item — or null when an identical item is already
	/// waiting and this request was folded into it.
	/// </summary>
	/// <param name="title">What the user sees in the queue.</param>
	/// <param name="organization">The organisation the work is scoped to, or null for all of them.</param>
	/// <param name="dedupKey">Identifies work that would repeat what is already pending.</param>
	/// <param name="ownerId">The component that will execute the item.</param>
	/// <param name="run">The work itself.</param>
	public WorkItem? Enqueue(
		string title,
		string? organization,
		string dedupKey,
		object ownerId,
		Func<IProgress<string>, CancellationToken, Task> run)
	{
		WorkItem item;

		lock (_lock)
		{
			// Folded against pending items only: the running one may already be returning a stale
			// picture, so asking for it again earns a fresh pass rather than being swallowed.
			if (_items.Any(i => i.State == WorkItemState.Pending
				&& string.Equals(i.DedupKey, dedupKey, StringComparison.Ordinal)))
			{
				return null;
			}

			item = new WorkItem
			{
				Id = (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture),
				Title = title,
				Organization = organization,
				DedupKey = dedupKey,
				OwnerId = ownerId,
				Run = run
			};

			_items.Add(item);
		}

		Changed?.Invoke();
		return item;
	}

	/// <summary>
	/// Claims the next item for execution, if the queue is idle and the head of the queue belongs to
	/// the caller. Returns false otherwise, which is what keeps the queue single-flight.
	/// </summary>
	/// <param name="ownerId">The component asking for work.</param>
	/// <param name="item">The claimed item, when this returns true.</param>
	public bool TryDequeueForExecution(object ownerId, out WorkItem item)
	{
		lock (_lock)
		{
			item = null!;

			if (_running is not null)
			{
				return false;
			}

			// The head is never skipped: letting a later item jump the queue because the head's owner
			// is busy would make the visible order a lie. An owner that has gone away is cleared out
			// by RemoveOwnedBy instead.
			var head = _items.Find(i => i.State == WorkItemState.Pending);
			if (head is null || !ReferenceEquals(head.OwnerId, ownerId))
			{
				return false;
			}

			head.State = WorkItemState.Running;
			_running = head;
			_runningCts = new CancellationTokenSource();
			item = head;
		}

		Changed?.Invoke();
		return true;
	}

	/// <summary>
	/// The token for the running item, or null when <paramref name="id"/> is not the running item.
	/// </summary>
	public CancellationToken? Token(string id)
	{
		lock (_lock)
		{
			return _running is not null && _running.Id == id ? _runningCts?.Token : null;
		}
	}

	/// <summary>
	/// Records progress within the running item, e.g. "repo 8 of 47".
	/// </summary>
	public void ReportProgress(WorkItem item, string progress)
	{
		item.Progress = progress;
		Changed?.Invoke();
	}

	/// <summary>
	/// Marks an item finished and lets the next one run.
	/// </summary>
	/// <param name="item">The item that has stopped executing.</param>
	/// <param name="error">The exception it failed with, or null if it did not.</param>
	public void Complete(WorkItem item, Exception? error)
	{
		lock (_lock)
		{
			item.State = item.State == WorkItemState.Cancelling || error is OperationCanceledException
				? WorkItemState.Cancelled
				: error is null ? WorkItemState.Completed : WorkItemState.Failed;

			_items.Remove(item);

			if (ReferenceEquals(_running, item))
			{
				_runningCts?.Dispose();
				_runningCts = null;
				_running = null;
			}
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Stops an item: a pending one is removed, and the running one is signalled to unwind, reverting
	/// anything it has half-applied.
	/// </summary>
	public void Cancel(string id)
	{
		lock (_lock)
		{
			if (_running is not null && _running.Id == id)
			{
				_running.State = WorkItemState.Cancelling;
				_runningCts?.Cancel();
			}
			else
			{
				var pending = _items.Find(i => i.Id == id && i.State == WorkItemState.Pending);
				if (pending is null)
				{
					return;
				}

				pending.State = WorkItemState.Cancelled;
				_items.Remove(pending);
			}
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Removes a pending item. The running item is left alone: stopping that one is
	/// <see cref="Cancel"/>'s job, because it has to be unwound rather than dropped.
	/// </summary>
	public void Remove(string id)
	{
		lock (_lock)
		{
			var pending = _items.Find(i => i.Id == id && i.State == WorkItemState.Pending);
			if (pending is null)
			{
				return;
			}

			pending.State = WorkItemState.Cancelled;
			_items.Remove(pending);
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Drops everything an owner had waiting and stops what it had running. Called when a circuit goes
	/// away: its work cannot execute without it, and leaving the item at the head would stall the
	/// whole queue.
	/// </summary>
	public void RemoveOwnedBy(object ownerId)
	{
		lock (_lock)
		{
			foreach (var pending in _items
				.Where(i => i.State == WorkItemState.Pending && ReferenceEquals(i.OwnerId, ownerId))
				.ToList())
			{
				pending.State = WorkItemState.Cancelled;
				_items.Remove(pending);
			}

			if (_running is not null && ReferenceEquals(_running.OwnerId, ownerId))
			{
				_running.State = WorkItemState.Cancelling;
				_runningCts?.Cancel();
			}
		}

		Changed?.Invoke();
	}
}
