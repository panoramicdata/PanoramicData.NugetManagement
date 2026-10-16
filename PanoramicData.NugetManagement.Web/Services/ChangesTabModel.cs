namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What the Changes tab shows: the changed files, a label per file, the selected file and its diff.
/// Holds the logic the view needs apart from rendering, so it can be tested without a renderer.
/// </summary>
/// <param name="reader">Reads the clone.</param>
/// <param name="minimumInterval">The least time between two reads of the clone.</param>
internal sealed class ChangesTabModel(CloneChangeReader reader, TimeSpan minimumInterval)
{
	/// <summary>Label for a file that was already changed when the session started and has not been touched since.</summary>
	public const string PreExistingLabel = "(pre-existing)";

	/// <summary>Label for a file that was already changed when the session started and has been edited since.</summary>
	public const string EditedLabel = "(pre-existing, edited by this session)";

	private readonly Lock _gate = new();
	private DateTime _lastRead = DateTime.MinValue;
	private bool _reading;
	private Snapshot _current = Snapshot.Empty;

	/// <summary>The latest result. Replaced as a whole, so a reader on another thread never sees a half-built one.</summary>
	public Snapshot Current => Volatile.Read(ref _current);

	/// <summary>Chooses the selected path: the current one if it is still changed, otherwise the first change.</summary>
	/// <param name="selected">The path selected so far, if any.</param>
	/// <param name="changes">The changed files now.</param>
	public static string? ResolveSelection(string? selected, IReadOnlyList<ChangedFile> changes)
		=> selected is not null && changes.Any(c => c.Path == selected)
			? selected
			: changes.FirstOrDefault()?.Path;

	/// <summary>The label for a change, or null when the file was not already changed at session start.</summary>
	/// <param name="path">The repository-relative path.</param>
	/// <param name="preExisting">Path to fingerprint, captured when the session started.</param>
	/// <param name="currentFingerprint">The file's fingerprint now.</param>
	public static string? LabelFor(string path, IReadOnlyDictionary<string, string> preExisting, Func<string> currentFingerprint)
		=> preExisting.TryGetValue(path, out var baseline)
			? (string.Equals(baseline, currentFingerprint(), StringComparison.Ordinal) ? PreExistingLabel : EditedLabel)
			: null;

	/// <summary>
	/// Claims the right to read the clone: false when a read is under way, or when the last one was
	/// less than the minimum interval ago and <paramref name="force"/> is not set. Pair with <see cref="Refresh"/>.
	/// </summary>
	/// <param name="now">The current time.</param>
	/// <param name="force">Skip the interval check (the overlap check still applies).</param>
	public bool TryBeginRead(DateTime now, bool force = false)
	{
		lock (_gate)
		{
			if (_reading || (!force && now - _lastRead < minimumInterval))
			{
				return false;
			}

			_reading = true;
			_lastRead = now;
			return true;
		}
	}

	/// <summary>Reads the clone and publishes a new <see cref="Current"/>. Call after <see cref="TryBeginRead"/> succeeds. Never throws.</summary>
	/// <param name="clonePath">The clone's directory.</param>
	/// <param name="selected">The path the user has selected, if any.</param>
	/// <param name="preExisting">Path to fingerprint, captured when the session started.</param>
	public void Refresh(string clonePath, string? selected, IReadOnlyDictionary<string, string> preExisting)
	{
		try
		{
			var changes = reader.ReadChanges(clonePath);
			var labels = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var change in changes)
			{
				var label = LabelFor(change.Path, preExisting, () => reader.Fingerprint(clonePath, change));
				if (label is not null)
				{
					labels[change.Path] = label;
				}
			}

			var path = ResolveSelection(selected, changes);
			var file = changes.FirstOrDefault(c => c.Path == path);
			var pair = file is null ? null : reader.ReadPair(clonePath, file);
			Volatile.Write(ref _current, new Snapshot(changes, labels, path, pair));
		}
		catch (Exception)
		{
			// Never into the render: keep showing the last good result.
		}
		finally
		{
			lock (_gate)
			{
				_reading = false;
			}
		}
	}

	/// <summary>One consistent result of reading the clone.</summary>
	/// <param name="Changes">The changed files.</param>
	/// <param name="Labels">Label per path, only for files that were already changed at session start.</param>
	/// <param name="SelectedPath">The file whose diff is shown.</param>
	/// <param name="Pair">The selected file's two sides.</param>
	public sealed record Snapshot(
		IReadOnlyList<ChangedFile> Changes,
		IReadOnlyDictionary<string, string> Labels,
		string? SelectedPath,
		FilePair? Pair)
	{
		/// <summary>Nothing read yet.</summary>
		public static Snapshot Empty { get; } = new([], new Dictionary<string, string>(), null, null);
	}
}
