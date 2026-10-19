using System.Text.Json;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Remembers the package ids a repository's project files declared, for as long as nobody has pushed to it.
/// </summary>
/// <remarks>
/// <para>
/// Finding which unpublished repositories declare a <c>PackageId</c> costs GitHub API calls: a tree listing
/// and one or more file reads for every such repository, on every discovery. The answer only changes when
/// the repository does, and GitHub says when that last happened (<c>pushed_at</c>), so an answer is kept
/// against that timestamp and reused until it moves.
/// </para>
/// <para>
/// "Declares nothing" is remembered as firmly as "declares something": the repositories that declare
/// nothing are the majority, and re-reading them is where most of the calls went.
/// </para>
/// <para>
/// Held in a file under local application data rather than the repository, because it is a record of what
/// this machine has already asked GitHub, not part of the estate's state. A lost or unreadable file only
/// means the next discovery asks again.
/// </para>
/// </remarks>
public sealed class DeclaredPackageIdCache
{
	private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

	private readonly string? _path;
	private readonly Lock _lock = new();
	private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
	private bool _dirty;

	/// <summary>
	/// Initializes a new instance, loading any persisted answers.
	/// </summary>
	/// <param name="path">The JSON file path, or null to remember in memory only.</param>
	public DeclaredPackageIdCache(string? path)
	{
		_path = path;
		Load();
	}

	/// <summary>
	/// The cache file's location under the user's local application data.
	/// </summary>
	public static string DefaultPath() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"PanoramicData.NugetManagement",
		"declared-package-ids.json");

	/// <summary>
	/// Gets what was recorded for a repository, provided nobody has pushed to it since.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="pushedAt">When GitHub says the repository was last pushed to.</param>
	/// <param name="packageIds">The package ids it declared then; empty when it declared none.</param>
	/// <returns>True when an answer is held for exactly this push.</returns>
	public bool TryGet(string repositoryFullName, DateTimeOffset pushedAt, out IReadOnlyList<string> packageIds)
	{
		lock (_lock)
		{
			if (_entries.TryGetValue(repositoryFullName, out var entry) && entry.PushedAtUtc == pushedAt.UtcDateTime)
			{
				packageIds = entry.PackageIds;
				return true;
			}
		}

		packageIds = [];
		return false;
	}

	/// <summary>
	/// Records what a repository declared as at its last push.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="pushedAt">When GitHub says the repository was last pushed to.</param>
	/// <param name="packageIds">The package ids it declares; empty when it declares none.</param>
	public void Set(string repositoryFullName, DateTimeOffset pushedAt, IReadOnlyList<string> packageIds)
	{
		lock (_lock)
		{
			_entries[repositoryFullName] = new Entry(pushedAt.UtcDateTime, [.. packageIds]);
			_dirty = true;
		}
	}

	/// <summary>
	/// Writes any answers recorded since the last save. Best-effort: a read-only environment just
	/// means the next discovery asks again.
	/// </summary>
	public void Save()
	{
		if (string.IsNullOrEmpty(_path))
		{
			return;
		}

		lock (_lock)
		{
			if (!_dirty)
			{
				return;
			}

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
				File.WriteAllText(_path, JsonSerializer.Serialize(_entries, _jsonOptions));
				_dirty = false;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Leaves _dirty set, so the next save tries again.
			}
		}
	}

	private void Load()
	{
		if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
		{
			return;
		}

		try
		{
			var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path));
			if (loaded is null)
			{
				return;
			}

			foreach (var (name, entry) in loaded)
			{
				_entries[name] = entry;
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			// Unreadable: start empty. Every answer is rebuilt from GitHub, which is slower, not wrong.
			_entries.Clear();
		}
	}

	private sealed record Entry(DateTime PushedAtUtc, List<string> PackageIds);
}
