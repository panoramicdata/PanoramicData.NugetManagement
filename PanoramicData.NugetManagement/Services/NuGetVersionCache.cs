using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// What nuget.org last reported about one package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="LatestVersion">The latest stable version published.</param>
/// <param name="Published">When that version was published, per nuget.org.</param>
/// <param name="RefreshedAtUtc">When this entry last changed.</param>
public sealed record NuGetVersionSnapshot(
	string PackageId,
	string LatestVersion,
	DateTimeOffset Published,
	DateTimeOffset RefreshedAtUtc);

/// <summary>
/// A committed snapshot of the latest stable version of each package the estate depends on.
/// </summary>
/// <remarks>
/// <para>
/// The rules read this and never contact nuget.org, so an assessment is a pure function of the
/// repository plus this file: reproducible, offline, and moving only when a refresh is committed.
/// Resolving "latest" live meant a repository that changed nothing turned red because a stranger
/// published a patch.
/// </para>
/// <para>
/// A miss is "unknown", never a guess. An absent or corrupt file therefore disables the upstream
/// half of the gate entirely rather than inventing versions to judge repositories against.
/// </para>
/// </remarks>
public sealed class NuGetVersionCache
{
	/// <summary>The file this cache is persisted to, at the scanner repository root.</summary>
	public const string FileName = "nuget-versions.json";

	private readonly string? _filePath;
	private readonly ConcurrentDictionary<string, NuGetVersionSnapshot> _snapshots;

	/// <summary>
	/// Guards <see cref="Update"/>'s read-then-write pair. A <see cref="ConcurrentDictionary{TKey,TValue}"/>
	/// makes each of <c>TryGetValue</c> and the indexer individually thread-safe, but not the two
	/// together: a rate-limited refresher runs several lookups for distinct packages concurrently, and
	/// without this lock two sweeps racing on the same package id could both observe "changed" and
	/// double-count, or interleave and lose an update.
	/// </summary>
	private readonly Lock _updateLock = new();

	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		WriteIndented = true
	};

	/// <summary>
	/// Initializes a new instance, loading any persisted snapshot.
	/// </summary>
	/// <param name="filePath">The JSON file path, or null to operate in memory only.</param>
	public NuGetVersionCache(string? filePath)
	{
		_filePath = filePath;
		_snapshots = new(Load(filePath), StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The snapshot for a package, if one has been recorded.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	/// <param name="snapshot">The snapshot, when found.</param>
	public bool TryGet(string packageId, out NuGetVersionSnapshot snapshot)
		=> _snapshots.TryGetValue(packageId, out snapshot!);

	/// <summary>
	/// Records what nuget.org reported for a package, and says whether that changed anything.
	/// </summary>
	/// <remarks>
	/// Returns false when the version is the one already held, and leaves
	/// <see cref="NuGetVersionSnapshot.RefreshedAtUtc"/> untouched in that case. The file is committed,
	/// so a sweep that stamped a new timestamp on every package each interval would leave the working
	/// tree permanently modified and bury real version changes in noise.
	/// </remarks>
	/// <param name="packageId">The package identifier.</param>
	/// <param name="latestVersion">The latest stable version nuget.org reports.</param>
	/// <param name="published">When that version was published.</param>
	/// <param name="now">The current time, for stamping a genuine change.</param>
	/// <returns>True when the stored version changed.</returns>
	public bool Update(string packageId, string latestVersion, DateTimeOffset published, DateTimeOffset now)
	{
		// The read (TryGetValue) and the write (the indexer) are each individually thread-safe on a
		// ConcurrentDictionary, but the pair is not atomic. A rate-limited refresher can run several
		// packages' updates concurrently, so the two steps are serialised here to stop two sweeps on
		// the same package id from both observing "changed" or from interleaving and losing an update.
		lock (_updateLock)
		{
			if (_snapshots.TryGetValue(packageId, out var existing)
				&& string.Equals(existing.LatestVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			_snapshots[packageId] = new NuGetVersionSnapshot(packageId, latestVersion, published, now);
			return true;
		}
	}

	/// <summary>
	/// Writes the cache to its file. Best-effort: a read-only environment simply keeps what it has.
	/// </summary>
	public void Persist()
	{
		if (string.IsNullOrEmpty(_filePath))
		{
			return;
		}

		try
		{
			var ordered = _snapshots
				.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(
					kvp => kvp.Key,
					kvp => new Entry(kvp.Value.LatestVersion, kvp.Value.Published, kvp.Value.RefreshedAtUtc));

			File.WriteAllText(_filePath, JsonSerializer.Serialize(ordered, _jsonOptions));
		}
		catch
		{
			// Read-only environment (for example a deployed server): persistence is best-effort.
		}
	}

	/// <summary>An entry as stored on disk, keyed by package id.</summary>
	private sealed record Entry(
		[property: JsonPropertyName("latestVersion")] string LatestVersion,
		[property: JsonPropertyName("published")] DateTimeOffset Published,
		[property: JsonPropertyName("refreshedAtUtc")] DateTimeOffset RefreshedAtUtc);

	private static Dictionary<string, NuGetVersionSnapshot> Load(string? filePath)
	{
		var result = new Dictionary<string, NuGetVersionSnapshot>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
		{
			return result;
		}

		try
		{
			var parsed = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
				File.ReadAllText(filePath),
				_jsonOptions);

			if (parsed is not null)
			{
				foreach (var (packageId, entry) in parsed)
				{
					result[packageId] = new NuGetVersionSnapshot(
						packageId,
						entry.LatestVersion,
						entry.Published,
						entry.RefreshedAtUtc);
				}
			}
		}
		catch
		{
			// Corrupt or unreadable: every package stays unknown, which disables the upstream half of
			// the gate rather than judging repositories against invented versions.
		}

		return result;
	}

	// ── Process-wide default instance used by the rules ──

	private static NuGetVersionCache? _default;

	/// <summary>
	/// The shared cache used during assessment. Assignable so tests can substitute an in-memory
	/// instance (constructed with a null path) that never reads or writes the committed file.
	/// </summary>
	public static NuGetVersionCache Default
	{
		get => _default ??= new NuGetVersionCache(RepositoryRootFile.Resolve(FileName));
		set => _default = value;
	}
}
