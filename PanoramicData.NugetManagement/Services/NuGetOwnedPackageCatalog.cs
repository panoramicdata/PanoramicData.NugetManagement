using System.Collections.Concurrent;
using System.Text.Json;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// A record of which NuGet packages the estate publishes itself.
/// </summary>
/// <remarks>
/// <para>
/// Learned from NuGet's <c>owner:</c> search during package discovery, which is already how the
/// application finds the repositories it governs, and persisted to <c>nuget-owned.json</c> at the
/// scanner repository root. Read by the freshness rules, which give a release of ours no grace
/// period: the grace exists so a verdict is not handed to whoever published this morning, and for a
/// package we published there is nobody else to wait on.
/// </para>
/// <para>
/// Owner-based rather than a package-id prefix, because plenty of the estate's packages —
/// <c>AutoTask.Api</c>, <c>Highlight.Api</c> — carry no common prefix, and a prefix list would
/// silently leave those on the full grace period with nothing to show why.
/// </para>
/// <para>
/// Additive: a package recorded once stays recorded. Discovery pages through a search API, and a
/// throttled or truncated sweep returning fewer packages must not quietly restore the grace period
/// on the ones it missed. A package we have published is ours whether or not today's search found it.
/// </para>
/// </remarks>
public sealed class NuGetOwnedPackageCatalog
{
	/// <summary>The file this catalogue is persisted to, at the scanner repository root.</summary>
	public const string FileName = "nuget-owned.json";

	private readonly string? _filePath;
	private readonly ConcurrentDictionary<string, byte> _owned;
	private readonly Lock _persistLock = new();

	private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

	/// <summary>
	/// Initializes a new instance, loading any persisted list.
	/// </summary>
	/// <param name="filePath">The JSON file path, or null to operate in memory only.</param>
	public NuGetOwnedPackageCatalog(string? filePath)
	{
		_filePath = filePath;
		var loaded = Load(filePath, out var loadFailure);
		LoadFailure = loadFailure;
		_owned = new(loaded.Select(id => new KeyValuePair<string, byte>(id, 0)), StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Why the file could not be read, or null when there was nothing to read or reading succeeded.
	/// </summary>
	public string? LoadFailure { get; }

	/// <summary>
	/// Whether a file was present but could not be read.
	/// </summary>
	/// <remarks>
	/// An unreadable catalogue restores the full grace period to every package, which looks exactly
	/// like an estate that publishes nothing. That is the safe direction — no repository is failed on
	/// the strength of a file nobody could read — but it is still worth being able to tell the two
	/// apart.
	/// </remarks>
	public bool LoadFailed => LoadFailure is not null;

	/// <summary>Every package id known to be ours.</summary>
	public IReadOnlyCollection<string> PackageIds => [.. _owned.Keys];

	/// <summary>
	/// Whether the estate publishes this package itself.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	/// <remarks>
	/// Case-insensitive: ids reach this from NuGet search, from a project file and from a Dependabot
	/// pull request title, and nothing makes the three agree on casing.
	/// </remarks>
	public bool Contains(string packageId) => _owned.ContainsKey(packageId);

	/// <summary>
	/// Records the packages a discovery sweep found the estate publishing, persisting any new ones.
	/// </summary>
	/// <param name="packageIds">The package ids discovered.</param>
	public void Record(IEnumerable<string> packageIds)
	{
		lock (_persistLock)
		{
			var changed = false;

			foreach (var packageId in packageIds)
			{
				if (string.IsNullOrWhiteSpace(packageId))
				{
					continue;
				}

				changed |= _owned.TryAdd(packageId.Trim(), 0);
			}

			// Only a new package rewrites the file. Discovery runs at every start-up and returns the
			// same list almost every time, and this file is committed: churning it on each run would
			// put an empty diff in front of whoever next commits.
			if (changed)
			{
				Persist();
			}
		}
	}

	private void Persist()
	{
		if (string.IsNullOrEmpty(_filePath))
		{
			return;
		}

		try
		{
			var ordered = _owned.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();

			File.WriteAllText(_filePath, JsonSerializer.Serialize(ordered, _jsonOptions));
		}
		catch
		{
			// Read-only environment (for example a deployed server): recording is best-effort, and the
			// committed file from the last machine that could write still stands.
		}
	}

	private static List<string> Load(string? filePath, out string? loadFailure)
	{
		loadFailure = null;
		var result = new List<string>();
		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
		{
			// Absent is normal before discovery has ever run, and is not a failure.
			return result;
		}

		try
		{
			var parsed = JsonSerializer.Deserialize<string[]>(File.ReadAllText(filePath));
			if (parsed is not null)
			{
				result.AddRange(parsed.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
			}
		}
		catch (Exception ex)
		{
			loadFailure = $"{filePath}: {ex.Message}";
			result.Clear();
		}

		return result;
	}

	// ── Process-wide default instance used by the rules ──

	private static NuGetOwnedPackageCatalog? _default;

	/// <summary>
	/// The shared catalogue used during assessment. Assignable so tests can substitute an in-memory
	/// instance (constructed with a null path) that never reads or writes the committed file.
	/// </summary>
	public static NuGetOwnedPackageCatalog Default
	{
		get => _default ??= new NuGetOwnedPackageCatalog(RepositoryRootFile.Resolve(FileName));
		set => _default = value;
	}
}
