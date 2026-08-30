using System.Globalization;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// The application's work queues: one lane per repository, one per organisation, running
/// concurrently up to a cap.
/// </summary>
/// <remarks>
/// This replaces a single application-wide queue. That queue serialised the whole estate in order to
/// protect one working tree at a time, which meant fixing one repository blocked building another
/// that shared nothing with it. The invariant is kept but narrowed: one item at a time
/// <em>within a lane</em>, many lanes at once across the estate.
/// <para>
/// The service coordinates but does not execute. <see cref="WorkRunnerService"/> pumps it.
/// </para>
/// </remarks>
public sealed class WorkLaneService
{
	private readonly Lock _lock = new();
	private readonly Dictionary<string, WorkLane> _lanes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, CancellationTokenSource> _tokenSources = new(StringComparer.Ordinal);
	private int _nextId;
	private long _nextLaneSequence;
	private int _maxConcurrentLanes = 20;

	/// <summary>
	/// Raised whenever any lane changes: an item is added, starts, reports progress, or finishes.
	/// </summary>
	/// <remarks>
	/// Raised from whatever thread the change happened on, and — with twenty lanes reporting progress
	/// — often. Subscribers rendering from it must debounce; see the navigation tree.
	/// <para>
	/// This is the UI's event, not the runner's — see <see cref="QueueChanged"/> for the distinction.
	/// </para>
	/// </remarks>
	public event Action? Changed;

	/// <summary>
	/// Raised whenever what is queued changes: an item is added, started, finished, cancelled or
	/// removed — as distinct from <see cref="Changed"/>, which also fires for a progress report and is
	/// what the UI renders from.
	/// </summary>
	/// <remarks>
	/// A progress line is not a change to what is queued, so it does not raise this event. That is what
	/// lets <see cref="WorkRunnerService"/> use it to decide when to save the queue and when to wake the
	/// pump, without rewriting the queue file on every one of up to twenty lanes' progress reports.
	/// </remarks>
	public event Action? QueueChanged;

	/// <summary>
	/// How many lanes may execute at once. Lowering it does not stop lanes already running; it takes
	/// effect as they drain.
	/// </summary>
	public int MaxConcurrentLanes
	{
		get { lock (_lock) { return _maxConcurrentLanes; } }
		set { lock (_lock) { _maxConcurrentLanes = Math.Max(1, value); } }
	}

	/// <summary>Every lane with outstanding work.</summary>
	public IReadOnlyList<WorkLane> Lanes
	{
		get
		{
			lock (_lock)
			{
				// Snapshots, not the live lanes: WorkLane.Items is a mutable list that every locked
				// mutator writes to, and a caller rendering from Changed would otherwise enumerate it
				// while the runner adds to it. The WorkItem instances themselves stay shared by
				// reference — the UI reads their live State/Progress — only the lane and its list
				// are copied.
				return [.. _lanes.Values.Select(lane =>
				{
					var copy = new WorkLane
					{
						Key = lane.Key,
						Organization = lane.Organization,
						RepositoryFullName = lane.RepositoryFullName,
						IsRunning = lane.IsRunning,
						Sequence = lane.Sequence
					};
					copy.Items.AddRange(lane.Items);
					return copy;
				})];
			}
		}
	}

	/// <summary>How many lanes are executing.</summary>
	public int RunningLaneCount
	{
		get { lock (_lock) { return _lanes.Values.Count(l => l.IsRunning); } }
	}

	/// <summary>The outstanding work in one lane, running item first.</summary>
	/// <param name="laneKey">The lane, as built by <see cref="WorkDescriptor.LaneKey"/>.</param>
	public IReadOnlyList<WorkItem> ItemsFor(string laneKey)
	{
		lock (_lock)
		{
			return _lanes.TryGetValue(laneKey, out var lane) ? [.. lane.Items] : [];
		}
	}

	/// <summary>
	/// Adds work to its lane, returning the queued item — or null when an identical item is already
	/// waiting in that lane and this request was folded into it.
	/// </summary>
	/// <param name="title">What the user sees in the tree.</param>
	/// <param name="descriptor">What the work will do.</param>
	/// <param name="dedupKey">Identifies work that would repeat what is already pending in this lane.</param>
	/// <param name="step">The workflow step this work performs, or null when it is not one.</param>
	/// <param name="consoleNodeKey">The console its output belongs to.</param>
	/// <param name="wasInterrupted">Whether this item is being restored after the process stopped mid-run.</param>
	/// <param name="foldDuplicates">
	/// Whether a matching pending item should swallow this request. False when restoring a saved
	/// queue: those items were already judged distinct when they were queued, and folding them now
	/// would silently drop work the user is owed.
	/// </param>
	public WorkItem? Enqueue(
		string title,
		WorkDescriptor descriptor,
		string dedupKey,
		WorkflowStep? step,
		string? consoleNodeKey,
		bool wasInterrupted = false,
		bool foldDuplicates = true)
	{
		WorkItem item;

		lock (_lock)
		{
			var lane = GetOrAddLane(descriptor);

			// Folded against pending items only, and only within this lane: the running item may
			// already be returning a stale picture, so asking again earns a fresh pass rather than
			// being swallowed. Across lanes a shared key means two repositories, not one repeat.
			if (foldDuplicates && lane.Items.Any(i => i.State == WorkItemState.Pending
				&& string.Equals(i.DedupKey, dedupKey, StringComparison.Ordinal)))
			{
				return null;
			}

			item = new WorkItem
			{
				Id = (++_nextId).ToString(CultureInfo.InvariantCulture),
				Title = title,
				Descriptor = descriptor,
				DedupKey = dedupKey,
				Step = step,
				ConsoleNodeKey = consoleNodeKey,
				WasInterrupted = wasInterrupted
			};

			lane.Items.Add(item);
		}

		Changed?.Invoke();
		QueueChanged?.Invoke();
		return item;
	}

	/// <summary>
	/// Claims the next item that may start: the head of a lane that is idle, where starting it would
	/// not exceed <see cref="MaxConcurrentLanes"/>. Returns false when nothing may start.
	/// </summary>
	/// <param name="item">The claimed item, when this returns true.</param>
	public bool TryStartNext(out WorkItem item)
	{
		lock (_lock)
		{
			item = null!;

			if (_lanes.Values.Count(l => l.IsRunning) >= _maxConcurrentLanes)
			{
				return false;
			}

			// Ordered by Sequence, not by Dictionary enumeration order: a removed entry's slot can be
			// reused by a later insertion, so a lane that empties and is re-enqueued could otherwise
			// reclaim its old position ahead of a lane that has been waiting the whole time.
			var lane = _lanes.Values
				.Where(l => !l.IsRunning && l.Items.Any(i => i.State == WorkItemState.Pending))
				.OrderBy(l => l.Sequence)
				.FirstOrDefault();

			if (lane is null)
			{
				return false;
			}

			var head = lane.Items.Find(i => i.State == WorkItemState.Pending)!;
			head.State = WorkItemState.Running;
			lane.IsRunning = true;
			_tokenSources[head.Id] = new CancellationTokenSource();
			item = head;
		}

		Changed?.Invoke();
		QueueChanged?.Invoke();
		return true;
	}

	/// <summary>The token for a running item, or null when it is not running.</summary>
	/// <param name="id">The item's identifier.</param>
	public CancellationToken? Token(string id)
	{
		lock (_lock)
		{
			return _tokenSources.TryGetValue(id, out var source) ? source.Token : null;
		}
	}

	/// <summary>Records progress within an item, e.g. "repo 8 of 47".</summary>
	/// <param name="item">The running item.</param>
	/// <param name="progress">What to show.</param>
	public void ReportProgress(WorkItem item, string progress)
	{
		lock (_lock)
		{
			item.Progress = progress;
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Marks an item finished and frees its lane.
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

			if (_tokenSources.Remove(item.Id, out var source))
			{
				source.Dispose();
			}

			if (_lanes.TryGetValue(item.LaneKey, out var lane))
			{
				lane.Items.Remove(item);
				lane.IsRunning = false;

				// An empty lane is not a lane. Kept, it would render as a work node with nothing under it.
				if (lane.Items.Count == 0)
				{
					_lanes.Remove(item.LaneKey);
				}
			}
		}

		Changed?.Invoke();
		QueueChanged?.Invoke();
	}

	/// <summary>
	/// Stops an item: a pending one is removed, and a running one is signalled to unwind, reverting
	/// anything it has half-applied.
	/// </summary>
	/// <param name="id">The item's identifier.</param>
	public void Cancel(string id)
	{
		lock (_lock)
		{
			CancelLocked(id);
		}

		Changed?.Invoke();
		QueueChanged?.Invoke();
	}

	/// <summary>Removes a pending item. A running item is left to <see cref="Cancel"/>, which unwinds it.</summary>
	/// <param name="id">The item's identifier.</param>
	public void Remove(string id)
	{
		lock (_lock)
		{
			foreach (var lane in _lanes.Values.ToList())
			{
				var pending = lane.Items.Find(i => i.Id == id && i.State == WorkItemState.Pending);
				if (pending is null)
				{
					continue;
				}

				pending.State = WorkItemState.Cancelled;
				lane.Items.Remove(pending);
				RemoveLaneIfEmptyLocked(lane);
				break;
			}
		}

		Changed?.Invoke();
		QueueChanged?.Invoke();
	}

	/// <summary>Stops everything in one lane.</summary>
	/// <param name="laneKey">The lane to clear.</param>
	public void CancelLane(string laneKey)
	{
		lock (_lock)
		{
			if (_lanes.TryGetValue(laneKey, out var lane))
			{
				foreach (var item in lane.Items.ToList())
				{
					CancelLocked(item.Id);
				}
			}
		}

		Changed?.Invoke();
		QueueChanged?.Invoke();
	}

	/// <summary>
	/// Stops everything in every lane belonging to an organisation — its own lane and its
	/// repositories'. What the organisation node's "stop all" offers, now that a bulk action is many
	/// items rather than one.
	/// </summary>
	/// <param name="organization">The organisation to clear.</param>
	public void CancelUnder(string organization)
	{
		lock (_lock)
		{
			foreach (var lane in _lanes.Values
				.Where(l => string.Equals(l.Organization, organization, StringComparison.OrdinalIgnoreCase))
				.ToList())
			{
				foreach (var item in lane.Items.ToList())
				{
					CancelLocked(item.Id);
				}
			}
		}

		Changed?.Invoke();
		QueueChanged?.Invoke();
	}

	private void CancelLocked(string id)
	{
		foreach (var lane in _lanes.Values.ToList())
		{
			var item = lane.Items.Find(i => i.Id == id);
			if (item is null)
			{
				continue;
			}

			if (item.State == WorkItemState.Running)
			{
				item.State = WorkItemState.Cancelling;
				if (_tokenSources.TryGetValue(id, out var source))
				{
					source.Cancel();
				}
			}
			else if (item.State == WorkItemState.Pending)
			{
				item.State = WorkItemState.Cancelled;
				lane.Items.Remove(item);
				RemoveLaneIfEmptyLocked(lane);
			}

			return;
		}
	}

	private void RemoveLaneIfEmptyLocked(WorkLane lane)
	{
		if (lane.Items.Count == 0 && !lane.IsRunning)
		{
			_lanes.Remove(lane.Key);
		}
	}

	/// <summary>
	/// Everything outstanding, in a form that can be written to disk. A running item is recorded as
	/// having been running so that it can be cleaned up rather than resumed.
	/// </summary>
	public IReadOnlyList<PersistedWorkItem> Snapshot()
	{
		lock (_lock)
		{
			return
			[
				.. _lanes.Values
					.SelectMany(lane => lane.Items)
					.Where(i => i.State is WorkItemState.Pending or WorkItemState.Running or WorkItemState.Cancelling)
					.Select(i => new PersistedWorkItem(
						i.Title,
						i.Descriptor,
						i.DedupKey,
						i.Step,
						i.ConsoleNodeKey,
						i.State is WorkItemState.Running or WorkItemState.Cancelling))
			];
		}
	}

	/// <summary>
	/// Puts saved work back into its lanes at startup. Nothing is resumed mid-run: an item that was
	/// executing comes back pending and flagged, so the runner cleans its working tree first.
	/// </summary>
	/// <param name="items">What was saved.</param>
	public void Restore(IReadOnlyList<PersistedWorkItem> items)
	{
		foreach (var saved in items)
		{
			// Deduplication is judged once, when a request is first made. A snapshot already reflects
			// that judgement — a running item and a pending item sharing a dedup key were deliberately
			// both kept — so replaying it must not re-run the fold and silently drop the second one.
			Enqueue(
				saved.Title,
				saved.Descriptor,
				saved.DedupKey,
				saved.Step,
				saved.ConsoleNodeKey,
				wasInterrupted: saved.WasRunning,
				foldDuplicates: false);
		}
	}

	private WorkLane GetOrAddLane(WorkDescriptor descriptor)
	{
		var key = descriptor.LaneKey;
		if (_lanes.TryGetValue(key, out var lane))
		{
			return lane;
		}

		lane = new WorkLane
		{
			Key = key,
			Organization = descriptor.Organization,
			RepositoryFullName = descriptor.RepositoryFullName,
			Sequence = ++_nextLaneSequence
		};

		_lanes[key] = lane;
		return lane;
	}
}
