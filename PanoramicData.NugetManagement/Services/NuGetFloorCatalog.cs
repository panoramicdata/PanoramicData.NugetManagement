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
		var loaded = Load(filePath);
		_frozenBaseline = new(loaded, StringComparer.OrdinalIgnoreCase);
		_learned = new(loaded, StringComparer.OrdinalIgnoreCase);
	}

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

	private static Dictionary<string, NuGetVersion> Load(string? filePath)
	{
		var result = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
		{
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
		catch
		{
			// Corrupt or unreadable: no floors, so the consistency half of the gate stands down.
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
