using System.Collections.Concurrent;
using System.Text.Json;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// The best line and branch coverage a repository has been observed to reach.
/// </summary>
/// <param name="LinePercent">Line coverage, as a percentage.</param>
/// <param name="BranchPercent">Branch coverage, as a percentage.</param>
public readonly record struct CoverageBaseline(double LinePercent, double BranchPercent);

/// <summary>
/// A record of the best coverage each repository has reached, so coverage can be asked to increase
/// without inventing an estate-wide threshold that would suit no repository.
/// </summary>
/// <remarks>
/// The same ratchet <see cref="ActionVersionCatalog"/> applies to GitHub Action versions: a better
/// figure raises the floor and is persisted; a worse one is reported and changes nothing. A single
/// threshold across the estate would be the wrong instrument — this repository's own rules library
/// sits near 90% while its web layer is barely tested, and one number cannot describe both.
/// </remarks>
public sealed class CoverageBaselineCatalog
{
	private readonly string? _filePath;
	private readonly ConcurrentDictionary<string, CoverageBaseline> _baselines;
	private readonly Lock _persistLock = new();

	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	/// <summary>The file this catalogue is persisted to, relative to a repository root.</summary>
	public const string FileName = "coverage-baseline.json";

	/// <summary>
	/// Initializes a new instance, loading any persisted baselines.
	/// </summary>
	/// <param name="filePath">The JSON baseline file, or null to operate in memory only.</param>
	public CoverageBaselineCatalog(string? filePath)
	{
		_filePath = filePath;
		_baselines = new(Load(filePath), StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The best coverage recorded for a repository, or null when it has never been measured.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public CoverageBaseline? GetBaseline(string repositoryFullName)
		=> _baselines.TryGetValue(repositoryFullName, out var baseline) ? baseline : null;

	/// <summary>
	/// Records a measurement. A figure at or above the recorded best raises the floor and is
	/// persisted; a lower one is left alone, so the baseline only ever moves upwards.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="measured">The coverage just measured.</param>
	/// <returns>True when the baseline moved up.</returns>
	public bool Observe(string repositoryFullName, CoverageBaseline measured)
	{
		var existing = GetBaseline(repositoryFullName);
		if (existing is not null
			&& measured.LinePercent <= existing.Value.LinePercent
			&& measured.BranchPercent <= existing.Value.BranchPercent)
		{
			return false;
		}

		// Each figure ratchets on its own: branch coverage can improve while line coverage holds.
		var raised = new CoverageBaseline(
			Math.Max(measured.LinePercent, existing?.LinePercent ?? 0),
			Math.Max(measured.BranchPercent, existing?.BranchPercent ?? 0));

		_baselines[repositoryFullName] = raised;
		Persist();
		return true;
	}

	private static Dictionary<string, CoverageBaseline> Load(string? filePath)
	{
		if (filePath is null || !File.Exists(filePath))
		{
			return [];
		}

		try
		{
			var json = File.ReadAllText(filePath);
			return JsonSerializer.Deserialize<Dictionary<string, CoverageBaseline>>(json, _jsonOptions) ?? [];
		}
		catch (Exception ex) when (ex is JsonException or IOException)
		{
			// A malformed baseline is not worth failing an assessment over: it rebuilds from the next
			// measurement, and every repository simply reports as unmeasured until then.
			return [];
		}
	}

	private void Persist()
	{
		if (_filePath is null)
		{
			return;
		}

		lock (_persistLock)
		{
			try
			{
				var ordered = _baselines
					.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

				File.WriteAllText(_filePath, JsonSerializer.Serialize(ordered, _jsonOptions));
			}
			catch (IOException)
			{
				// Losing a baseline write costs a ratchet step, not correctness.
			}
		}
	}
}
