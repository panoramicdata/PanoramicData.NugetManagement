using System.Collections.Concurrent;
using System.Text.Json;
using NuGet.Versioning;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// A record of a learned floor change for a NuGet package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="From">The previous floor, or null when there was none.</param>
/// <param name="To">The newly-learned floor.</param>
/// <param name="Repository">The repository whose declaration triggered the learning.</param>
public sealed record NuGetFloorBump(string PackageId, string? From, string To, string? Repository);

/// <summary>
/// A self-updating store of the minimum-acceptable ("floor") version for each NuGet package.
/// </summary>
/// <remarks>
/// <para>
/// The floor is learned from the versions the organization's own repositories actually declare:
/// when a repository is observed on a higher version than the current floor, that becomes the new
/// floor and is persisted to <c>nuget-floors.json</c>. A single repository ahead of the pack is
/// enough — it is the canary, and it has proven the version works.
/// </para>
/// <para>
/// This asks a different question from nuget.org, which only reports what exists and has no opinion
/// on what we should be on. Gating on "newest in the world" made every repository fail whenever a
/// stranger published; gating on the estate's own best asks for consistency, which is achievable.
/// </para>
/// <para>
/// The floor used for pass/fail within a run is frozen at load time, so verdicts cannot shift
/// underneath a run in progress; observations raise the persisted floor for subsequent runs.
/// </para>
/// </remarks>
public sealed class NuGetFloorCatalog
{
	/// <summary>The file this catalogue is persisted to, at the scanner repository root.</summary>
	public const string FileName = "nuget-floors.json";

	private readonly string? _filePath;
	private readonly Dictionary<string, NuGetVersion> _frozenBaseline;
	private readonly ConcurrentDictionary<string, NuGetVersion> _learned;
	private readonly ConcurrentQueue<NuGetFloorBump> _bumps = new();
	private readonly Lock _persistLock = new();

	private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

	/// <summary>
	/// Initializes a new instance, loading any persisted floors.
	/// </summary>
	/// <param name="filePath">The JSON file path, or null to operate in memory only.</param>
	public NuGetFloorCatalog(string? filePath)
	{
		_filePath = filePath;
		var loaded = Load(filePath, out var loadFailure);
		LoadFailure = loadFailure;
		_frozenBaseline = new(loaded, StringComparer.OrdinalIgnoreCase);
		_learned = new(loaded, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Why the file could not be read, or null when there was nothing to read or reading succeeded.
	/// </summary>
	public string? LoadFailure { get; }

	/// <summary>
	/// Whether a file was present but could not be read.
	/// </summary>
	/// <remarks>
	/// An unreadable catalogue silently stands the consistency half of the gate down: every package
	/// has no floor, every repository passes, and the run looks healthy. An absent file is not a
	/// failure — nothing has been learned yet — but a file that exists and will not parse is.
	/// </remarks>
	public bool LoadFailed => LoadFailure is not null;

	/// <summary>
	/// Every package id this catalogue has a floor for, including those learned since it loaded.
	/// </summary>
	/// <remarks>
	/// This is the estate's package-id universe as observed by assessments: every rule evaluation
	/// calls <see cref="Observe"/> for each package reference it sees, so the set grows as the
	/// application assesses repositories. The refresher sweeps it, unioned with the version cache's
	/// ids, instead of rediscovering the estate for itself.
	/// </remarks>
	public IReadOnlyCollection<string> PackageIds => [.. _learned.Keys];

	/// <summary>The floor changes learned during this process, most recent last.</summary>
	public IReadOnlyList<NuGetFloorBump> RecentBumps => [.. _bumps];

	/// <summary>
	/// The floor for a package, or null when no repository has been seen using it. Stable for the
	/// lifetime of the process.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	public string? GetFloor(string packageId)
		=> _frozenBaseline.TryGetValue(packageId, out var version) ? version.ToNormalizedString() : null;

	/// <summary>
	/// Records the version a package was observed at. If it exceeds the current floor, the floor is
	/// raised and persisted, and a bump is recorded for surfacing in the UI.
	/// </summary>
	/// <param name="packageId">The package identifier.</param>
	/// <param name="version">The version the repository declares.</param>
	/// <param name="repository">The repository that declares it.</param>
	public void Observe(string packageId, string version, string? repository = null)
	{
		// Versions can be MSBuild properties rather than literals; those say nothing about the estate.
		if (!NuGetVersion.TryParse(version, out var observed))
		{
			return;
		}

		// A prerelease pin proves nothing, and the ratchet has no un-lower path. One repository
		// trying "11.0.0-beta.1" would otherwise raise a floor above every stable pin in the estate,
		// persist it to the committed file, and fail every other repository at PKG-07 (Critical) with
		// remediation text telling them to adopt a beta — undoable only by hand-editing the file.
		// The floor must also mean the same kind of version the cache means, and the cache is
		// deliberately stable-only (includePrerelease: false).
		if (observed.IsPrerelease)
		{
			return;
		}

		lock (_persistLock)
		{
			var current = _learned.TryGetValue(packageId, out var existing) ? existing : null;
			if (current is not null && observed <= current)
			{
				return;
			}

			_learned[packageId] = observed;
			_bumps.Enqueue(new NuGetFloorBump(
				packageId,
				current?.ToNormalizedString(),
				observed.ToNormalizedString(),
				repository));

			Persist();
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
			var ordered = _learned
				.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToNormalizedString());

			File.WriteAllText(_filePath, JsonSerializer.Serialize(ordered, _jsonOptions));
		}
		catch
		{
			// Read-only environment (for example a deployed server): learning is best-effort. A floor
			// learned in CI therefore evaporates, and only machines that commit move the bar.
		}
	}

	private static Dictionary<string, NuGetVersion> Load(string? filePath, out string? loadFailure)
	{
		loadFailure = null;
		var result = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
		{
			// Absent is normal before anything has been learned, and is not a failure.
			return result;
		}

		try
		{
			var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath));
			if (parsed is not null)
			{
				foreach (var (packageId, version) in parsed)
				{
					if (NuGetVersion.TryParse(version, out var floor))
					{
						result[packageId] = floor;
					}
				}
			}
		}
		catch (Exception ex)
		{
			// Corrupt or unreadable: no floors, so the consistency half of the gate stands down —
			// which looks exactly like a compliant estate. Recorded so an operator can tell the two
			// apart rather than swallowed without trace.
			loadFailure = $"{filePath}: {ex.Message}";
			result.Clear();
		}

		return result;
	}

	// ── Process-wide default instance used by the rules ──

	private static NuGetFloorCatalog? _default;

	/// <summary>
	/// The shared catalog used during assessment. Assignable so tests can substitute an in-memory
	/// instance (constructed with a null path) that never writes to the committed file.
	/// </summary>
	public static NuGetFloorCatalog Default
	{
		get => _default ??= new NuGetFloorCatalog(RepositoryRootFile.Resolve(FileName));
		set => _default = value;
	}
}
