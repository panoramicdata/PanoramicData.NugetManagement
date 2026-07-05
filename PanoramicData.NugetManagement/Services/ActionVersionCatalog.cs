using System.Collections.Concurrent;
using System.Text.Json;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// A record of a learned baseline change for a GitHub Action.
/// </summary>
/// <param name="Action">The action name (e.g. "actions/checkout").</param>
/// <param name="From">The previous floor spec (e.g. "v6").</param>
/// <param name="To">The newly-learned floor spec (e.g. "v7").</param>
/// <param name="Repository">The repository whose workflow triggered the learning.</param>
public sealed record ActionVersionBump(string Action, string From, string To, string? Repository);

/// <summary>
/// A self-updating store of the minimum-acceptable ("floor") major version for each GitHub Action.
/// </summary>
/// <remarks>
/// <para>
/// Rather than trusting a hardcoded notion of "latest", the floor is learned from the versions the
/// organization's own repositories actually use: when a repository is observed using a *higher*
/// version than the current floor, that version is recorded as the new floor and persisted to
/// <c>action-versions.json</c> at the repository root (so every run — and every teammate — assesses
/// against the same standard). A single repository ahead of the pack is enough (it is the canary).
/// </para>
/// <para>
/// The floor used for pass/fail within a single process is frozen at load time, so results are
/// stable during a run; observations raise the persisted floor for subsequent runs and are surfaced
/// via <see cref="RecentBumps"/>.
/// </para>
/// </remarks>
public sealed class ActionVersionCatalog
{
	private readonly string? _filePath;
	private readonly ConcurrentDictionary<string, int> _frozenBaseline;
	private readonly ConcurrentDictionary<string, int> _learned;
	private readonly ConcurrentQueue<ActionVersionBump> _bumps = new();
	private readonly Lock _persistLock = new();

	/// <summary>
	/// Initializes a new instance, loading any persisted baseline from <paramref name="filePath"/>.
	/// </summary>
	/// <param name="filePath">The JSON baseline file path, or null to operate in memory only.</param>
	public ActionVersionCatalog(string? filePath)
	{
		_filePath = filePath;
		var loaded = Load(filePath);
		_frozenBaseline = new(loaded, StringComparer.OrdinalIgnoreCase);
		_learned = new(loaded, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>The baseline changes learned during this process, most recent last.</summary>
	public IReadOnlyList<ActionVersionBump> RecentBumps => [.. _bumps];

	/// <summary>
	/// Gets the effective floor version spec for an action: the greater of the hardcoded default and
	/// the persisted learned value. Stable for the lifetime of the process.
	/// </summary>
	public string GetFloorSpec(string action, string hardcodedDefault)
	{
		var defaultMajor = GitHubActionVersion.ParseMajor(hardcodedDefault);
		var baseMajor = _frozenBaseline.TryGetValue(action, out var v) ? v : 0;
		return "v" + Math.Max(defaultMajor, baseMajor);
	}

	/// <summary>
	/// Records the version an action was observed at. If it exceeds the current floor, the floor is
	/// raised and persisted (self-update), and a bump is recorded for surfacing in the UI.
	/// </summary>
	public void Observe(string action, int observedMajor, string hardcodedDefault, string? repository = null)
	{
		var defaultMajor = GitHubActionVersion.ParseMajor(hardcodedDefault);

		lock (_persistLock)
		{
			var current = Math.Max(defaultMajor, _learned.TryGetValue(action, out var v) ? v : 0);
			if (observedMajor <= current)
			{
				return;
			}

			_learned[action] = observedMajor;
			_bumps.Enqueue(new ActionVersionBump(action, $"v{current}", $"v{observedMajor}", repository));
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
				.ToDictionary(kvp => kvp.Key, kvp => $"v{kvp.Value}");
			var json = JsonSerializer.Serialize(ordered, JsonOptions);
			File.WriteAllText(_filePath, json);
		}
		catch
		{
			// Read-only environment (e.g. a deployed server): learning is best-effort.
		}
	}

	private static Dictionary<string, int> Load(string? filePath)
	{
		var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
		{
			return result;
		}

		try
		{
			var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath));
			if (parsed is not null)
			{
				foreach (var (action, spec) in parsed)
				{
					result[action] = GitHubActionVersion.ParseMajor(spec);
				}
			}
		}
		catch
		{
			// Corrupt/unreadable file — fall back to hardcoded defaults.
		}

		return result;
	}

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	// ── Process-wide default instance used by the rules ──

	private static ActionVersionCatalog? _default;

	/// <summary>
	/// The shared catalog instance used during assessment. Assignable so tests can substitute an
	/// in-memory instance (constructed with a null path) that never writes to the committed file.
	/// </summary>
	public static ActionVersionCatalog Default
	{
		get => _default ??= new ActionVersionCatalog(ResolveDefaultPath());
		set => _default = value;
	}

	/// <summary>
	/// Resolves the path to <c>action-versions.json</c> at the scanner repository root (next to the
	/// solution file), walking up from the running assembly. Returns a best-effort path even when the
	/// file does not yet exist so it can be created on first learning.
	/// </summary>
	private static string? ResolveDefaultPath()
	{
		var dir = AppContext.BaseDirectory;
		while (dir is not null)
		{
			if (File.Exists(Path.Combine(dir, "PanoramicData.NugetManagement.slnx")))
			{
				return Path.Combine(dir, "action-versions.json");
			}

			dir = Directory.GetParent(dir)?.FullName;
		}

		return null;
	}
}
