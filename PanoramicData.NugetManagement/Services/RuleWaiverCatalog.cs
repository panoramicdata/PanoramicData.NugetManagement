using System.Text;
using System.Text.Json;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// The estate's record of which rules are deliberately waived for which repository.
/// </summary>
/// <remarks>
/// <para>
/// Persisted to <c>rule-waivers.json</c> at the scanner repository root, beside
/// <see cref="NuGetOwnedPackageCatalog.FileName"/> and for the same reason: a waiver is a governance
/// decision about the estate, so it belongs where the governance lives, reviewed in a pull request
/// and readable in the history. That is what separates it from an exclusion, which is one operator's
/// choice and lives in their own runtime settings.
/// </para>
/// <para>
/// Read-only. Waivers are written by hand, which is deliberate: a control that waives a rule in one
/// click is a control that waives a rule without a conversation.
/// </para>
/// </remarks>
public sealed class RuleWaiverCatalog
{
	/// <summary>The file this catalogue is read from, at the scanner repository root.</summary>
	public const string FileName = "rule-waivers.json";

	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	private static RuleWaiverCatalog? _default;

	private readonly Dictionary<string, List<RuleWaiver>> _byRepository;

	/// <summary>
	/// The shared catalogue used during assessment. Assignable so tests can substitute an in-memory
	/// instance (constructed with a null path) that never reads the committed file.
	/// </summary>
	public static RuleWaiverCatalog Default
	{
		get => _default ??= new RuleWaiverCatalog(RepositoryRootFile.Resolve(FileName));
		set => _default = value;
	}

	/// <summary>
	/// Initializes a new instance, loading any recorded waivers.
	/// </summary>
	/// <param name="filePath">The JSON file path, or null to operate with no waivers at all.</param>
	public RuleWaiverCatalog(string? filePath)
	{
		_byRepository = Load(filePath, out var loadFailure);
		LoadFailure = loadFailure;
	}

	/// <summary>
	/// Why the file could not be read, or which entries in it were refused — null when there was
	/// nothing to read and nothing wrong with what was read.
	/// </summary>
	public string? LoadFailure { get; }

	/// <summary>
	/// Whether a file was present but could not be read in full.
	/// </summary>
	/// <remarks>
	/// An unreadable catalogue waives nothing, which shows every repository the failures it actually
	/// has. That is the safe direction — no failure is ever hidden by a file nobody could read — but
	/// it is still worth being able to say so rather than appearing to have no waivers.
	/// </remarks>
	public bool LoadFailed => LoadFailure is not null;

	/// <summary>
	/// Every repository with at least one waiver, as recorded in the file.
	/// </summary>
	public IReadOnlyCollection<string> Repositories => [.. _byRepository.Keys];

	/// <summary>
	/// The waivers recorded for a repository, or an empty list when it has none.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <remarks>
	/// Case-insensitive: a full name reaches this from GitHub, from a dashboard row and from a
	/// hand-edited file, and nothing makes the three agree on casing.
	/// </remarks>
	public IReadOnlyList<RuleWaiver> For(string? repositoryFullName)
		=> repositoryFullName is not null
			&& _byRepository.TryGetValue(repositoryFullName.Trim(), out var waivers)
			? waivers
			: [];

	/// <summary>
	/// Reads the file, keeping the entries that are usable and reporting the ones that are not.
	/// </summary>
	private static Dictionary<string, List<RuleWaiver>> Load(string? filePath, out string? loadFailure)
	{
		loadFailure = null;
		var empty = new Dictionary<string, List<RuleWaiver>>(StringComparer.OrdinalIgnoreCase);

		if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
		{
			return empty;
		}

		Dictionary<string, List<RuleWaiver>?>? raw;
		try
		{
			raw = JsonSerializer.Deserialize<Dictionary<string, List<RuleWaiver>?>>(
				File.ReadAllText(filePath),
				_jsonOptions);
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			loadFailure = $"{Path.GetFileName(filePath)} could not be read: {ex.Message}";
			return empty;
		}

		if (raw is null)
		{
			return empty;
		}

		var refusals = new StringBuilder();
		var loaded = new Dictionary<string, List<RuleWaiver>>(StringComparer.OrdinalIgnoreCase);

		foreach (var (repository, waivers) in raw)
		{
			if (waivers is null)
			{
				continue;
			}

			var kept = new List<RuleWaiver>();
			foreach (var waiver in waivers)
			{
				// Refused one at a time rather than failing the file: one unusable entry is an unusable
				// entry, not grounds to drop every other waiver the estate has agreed.
				var refusal = Refusal(waiver);
				if (refusal is not null)
				{
					refusals.Append(refusals.Length == 0 ? string.Empty : " ")
						.Append(repository)
						.Append(": ")
						.Append(refusal);
					continue;
				}

				kept.Add(waiver);
			}

			if (kept.Count > 0)
			{
				loaded[repository.Trim()] = kept;
			}
		}

		if (refusals.Length > 0)
		{
			loadFailure = refusals.ToString();
		}

		return loaded;
	}

	/// <summary>
	/// Why a waiver cannot be used, or null when it can.
	/// </summary>
	private static string? Refusal(RuleWaiver? waiver)
	{
		if (waiver is null || string.IsNullOrWhiteSpace(waiver.RuleId))
		{
			return "a waiver names no rule, so it was ignored.";
		}

		return string.IsNullOrWhiteSpace(waiver.Reason)
			? $"the waiver for {waiver.RuleId} records no reason, so it was ignored."
			: null;
	}
}
