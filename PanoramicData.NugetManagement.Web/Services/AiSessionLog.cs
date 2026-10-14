using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Where AI fix session logs are kept, and for how long.
/// </summary>
/// <remarks>
/// One folder per day and one file per session, so a day's work can be read or deleted as a unit and a
/// session can be found by repository and rule from its name alone. Expiry is by file age rather than a
/// size cap: a month of sessions is the unit anyone reasons about, and it is what the setting says.
/// </remarks>
/// <param name="directory">The root folder.</param>
/// <param name="retentionDays">
/// How many days a session is kept; read on every purge so the setting can change without a restart.
/// Zero or less keeps everything.
/// </param>
public sealed class AiSessionLogStore(string directory, Func<int> retentionDays)
{
	/// <summary>Where logs go when nothing says otherwise.</summary>
	public static string DefaultDirectory { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"PanoramicData.NugetManagement",
		"logs",
		"ai-fix");

	/// <summary>The root folder the logs are written under.</summary>
	public string Directory { get; } = directory;

	/// <summary>
	/// Starts the log for one session, first discarding whatever has outlived retention.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="ruleId">The rule, or an identifier for the work.</param>
	public AiSessionLog Open(string repositoryFullName, string ruleId)
	{
		Purge();

		var now = DateTimeOffset.UtcNow;
		var name = $"{now:HHmmss}__{Sanitise(repositoryFullName)}__{Sanitise(ruleId)}.jsonl";
		var path = Path.Combine(Directory, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), name);

		return new AiSessionLog(path);
	}

	/// <summary>
	/// Deletes session files older than the retention period, and any day folder that leaves empty.
	/// </summary>
	/// <returns>How many files were deleted.</returns>
	/// <remarks>
	/// Never throws. A log that cannot be tidied is an annoyance, and a fix that fails because of it is
	/// a defect.
	/// </remarks>
	public int Purge()
	{
		var days = retentionDays();

		if (days <= 0 || !System.IO.Directory.Exists(Directory))
		{
			return 0;
		}

		var cutoff = DateTime.UtcNow.AddDays(-days);
		var deleted = 0;

		try
		{
			foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.jsonl", SearchOption.AllDirectories))
			{
				if (File.GetLastWriteTimeUtc(file) >= cutoff)
				{
					continue;
				}

				File.Delete(file);
				deleted++;
			}

			foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory))
			{
				if (!System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
				{
					System.IO.Directory.Delete(folder);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Left for the next purge.
		}

		return deleted;
	}

	private static string Sanitise(string value)
	{
		var builder = new StringBuilder(value.Length);

		foreach (var character in value)
		{
			builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_');
		}

		return builder.ToString();
	}
}

/// <summary>
/// The record of one AI fix session: everything the model was told and everything it did, a JSON
/// object per line.
/// </summary>
/// <remarks>
/// The pane's transcript is bounded, in memory and gone after a restart, which is the wrong shape for
/// the one question this exists to answer: why did a session go the way it did. This is unbounded per
/// session, written as the session runs so a crash still leaves it, and carries what the pane never
/// showed — each tool result in full, the model's reasoning, and the server's timings.
/// <para>
/// Every write is best-effort. Logging that can fail a fix would be worse than no logging.
/// </para>
/// </remarks>
public sealed class AiSessionLog : IDisposable
{
	private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

	private readonly Lock _gate = new();
	private StreamWriter? _writer;

	/// <summary>
	/// Opens the file the log writes to, creating its folder.
	/// </summary>
	/// <param name="path">The file.</param>
	public AiSessionLog(string path)
	{
		Path = path;

		try
		{
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

			// Shared for reading so the file can be tailed while the session is still running.
			_writer = new StreamWriter(
				new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
				new UTF8Encoding(false))
			{
				AutoFlush = true
			};
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_writer = null;
		}
	}

	/// <summary>A log that records nothing, for callers and tests that do not want one.</summary>
	public static AiSessionLog None { get; } = new(string.Empty, discard: true);

	private AiSessionLog(string path, bool discard)
		=> Path = discard ? string.Empty : path;

	/// <summary>The file this log is written to; empty when it records nothing.</summary>
	public string Path { get; }

	/// <summary>
	/// Records one event.
	/// </summary>
	/// <param name="type">What happened, for example <c>turn</c> or <c>tool_result</c>.</param>
	/// <param name="data">The event's fields.</param>
	public void Write(string type, IReadOnlyDictionary<string, object?> data)
	{
		if (_writer is null)
		{
			return;
		}

		var record = new Dictionary<string, object?>(data.Count + 2)
		{
			["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
			["type"] = type
		};

		foreach (var (key, value) in data)
		{
			record[key] = value;
		}

		try
		{
			lock (_gate)
			{
				_writer?.WriteLine(JsonSerializer.Serialize(record, _json));
			}
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
		{
			// A log line lost is not worth a session.
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		lock (_gate)
		{
			_writer?.Dispose();
			_writer = null;
		}
	}
}
