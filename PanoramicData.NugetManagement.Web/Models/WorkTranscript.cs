using System.Text;

namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// What produced a line of a work item's transcript, which is how the pane decides to render it.
/// </summary>
public enum WorkLineKind
{
	/// <summary>The executor's own narration: steps, tool calls, results, errors.</summary>
	Output,

	/// <summary>A model's reasoning, streamed from Ollama's <c>thinking</c> channel.</summary>
	Thinking,

	/// <summary>What a model actually said, as distinct from what it was thinking.</summary>
	Model,

	/// <summary>
	/// What the model was told before it said anything: the system prompt and the task.
	/// </summary>
	/// <remarks>
	/// Its own kind rather than ordinary output, because it is the one part of the transcript nobody
	/// wrote for a reader and the only part that explains the rest. A session that wanders is usually
	/// answering the prompt it was given rather than the one intended, and without this the reader is
	/// left inferring that prompt from the wandering.
	/// </remarks>
	Prompt
}

/// <summary>
/// One line of a work item's transcript.
/// </summary>
/// <param name="Kind">What produced it.</param>
/// <param name="Text">The line, without a trailing newline.</param>
/// <param name="AtUtc">When it was said.</param>
public sealed record WorkLine(WorkLineKind Kind, string Text, DateTimeOffset AtUtc);

/// <summary>
/// A work item's own output, bounded and safe to read while it is being written.
/// </summary>
/// <remarks>
/// Every executor line used to go only to the one shared UI console, so an item had nothing of its
/// own to show and two items running together interleaved into the same place. This is that missing
/// per-item record: the pane renders it, and it outlives the run so a finished session can still be
/// read.
/// <para>
/// Bounded on purpose. A streamed agentic session produces tokens without limit, and holding every
/// one of them for the lifetime of the item is how a queue turns into a memory leak.
/// </para>
/// <para>
/// The line currently being streamed into is held apart from the committed ones rather than rewritten
/// in place. A queue cannot be indexed, and rebuilding it on every token would make an agentic session
/// quadratic in its own output.
/// </para>
/// </remarks>
public sealed class WorkTranscript
{
	/// <summary>How many lines are kept before the oldest are dropped.</summary>
	public const int DefaultCapacity = 500;

	private readonly Lock _gate = new();
	private readonly Queue<WorkLine> _committed = new();
	private readonly int _capacity;

	private WorkLineKind _streamingKind;
	private StringBuilder? _streaming;
	private DateTimeOffset _streamingStartedUtc;

	/// <summary>
	/// Initializes a new instance holding at most <paramref name="capacity"/> lines.
	/// </summary>
	/// <param name="capacity">The cap; defaults to <see cref="DefaultCapacity"/>.</param>
	public WorkTranscript(int capacity = DefaultCapacity)
		=> _capacity = capacity > 0
			? capacity
			: throw new ArgumentOutOfRangeException(nameof(capacity), "A transcript must hold at least one line.");

	/// <summary>
	/// Appends a complete line.
	/// </summary>
	/// <param name="kind">What produced it.</param>
	/// <param name="text">The line.</param>
	public void Append(WorkLineKind kind, string text)
	{
		lock (_gate)
		{
			// Commits any streamed line first, so a tool call logged mid-stream is not glued onto
			// whatever the model happened to be saying when it was called.
			CommitStreaming();
			Add(new WorkLine(kind, text, DateTimeOffset.UtcNow));
		}
	}

	/// <summary>
	/// Appends a fragment of a streamed line, continuing the line in progress when the kind matches.
	/// </summary>
	/// <param name="kind">What produced it.</param>
	/// <param name="text">The fragment, which may be a single token.</param>
	/// <remarks>
	/// One transcript line per token would be unreadable and would exhaust the cap in seconds, so
	/// fragments of the same kind run together into one line. A change of kind ends the line: a model
	/// that stops thinking and starts speaking has said two different things.
	/// </remarks>
	public void AppendDelta(WorkLineKind kind, string text)
	{
		lock (_gate)
		{
			if (_streaming is not null && _streamingKind != kind)
			{
				CommitStreaming();
			}

			if (_streaming is null)
			{
				_streaming = new StringBuilder();
				_streamingKind = kind;
				_streamingStartedUtc = DateTimeOffset.UtcNow;
			}

			_streaming.Append(text);
		}
	}

	/// <summary>
	/// Ends the line the deltas are accumulating into, so the next fragment starts a new one.
	/// </summary>
	public void EndDelta()
	{
		lock (_gate)
		{
			CommitStreaming();
		}
	}

	/// <summary>
	/// The transcript as it stands, the line being streamed into included, and unaffected by anything
	/// written afterwards.
	/// </summary>
	/// <remarks>
	/// A copy, because the pane renders on the UI thread while the executor is still writing on its
	/// own — handing out the live queue would tear mid-render.
	/// </remarks>
	public IReadOnlyList<WorkLine> Snapshot()
	{
		lock (_gate)
		{
			if (_streaming is null)
			{
				return [.. _committed];
			}

			return [.. _committed, new WorkLine(_streamingKind, _streaming.ToString(), _streamingStartedUtc)];
		}
	}

	/// <summary>How many lines the transcript currently holds, the streamed one included.</summary>
	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _committed.Count + (_streaming is null ? 0 : 1);
			}
		}
	}

	private void CommitStreaming()
	{
		if (_streaming is null)
		{
			return;
		}

		Add(new WorkLine(_streamingKind, _streaming.ToString(), _streamingStartedUtc));
		_streaming = null;
	}

	private void Add(WorkLine line)
	{
		_committed.Enqueue(line);

		while (_committed.Count > _capacity)
		{
			_committed.Dequeue();
		}
	}
}
