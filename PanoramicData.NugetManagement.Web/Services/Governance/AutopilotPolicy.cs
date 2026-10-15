using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PanoramicData.NugetManagement.Web.Services.Governance;

/// <summary>When Autopilot may run, and how fast.</summary>
public sealed record AutopilotSchedule
{
	/// <summary>UTC windows it may run in, such as <c>08:00-20:00</c>, or null for any time.</summary>
	public IReadOnlyList<string>? WindowsUtc { get; init; }

	/// <summary>How long to leave a repository alone after a pass, so CI and Codacy can respond.</summary>
	public int CycleCooldownMinutes { get; init; } = 15;

	/// <summary>How many repositories may be in a cycle at once. The model is the bottleneck, so one.</summary>
	public int MaxConcurrentRepositories { get; init; } = 1;
}

/// <summary>Which repositories Autopilot may touch.</summary>
public sealed record AutopilotEligibility
{
	/// <summary>Whether a repository is enabled when it has no override.</summary>
	public bool DefaultEnabled { get; init; } = true;

	/// <summary>Per-repository switches, keyed "owner/name". An excluded repository is never active whatever this says.</summary>
	public IReadOnlyDictionary<string, bool> RepositoryOverrides { get; init; } = new Dictionary<string, bool>();

	/// <summary>Whether to refuse a clone with uncommitted changes. Always true in effect; see the design.</summary>
	public bool RequireCleanTree { get; init; } = true;

	/// <summary>Whether to skip archived repositories.</summary>
	public bool SkipArchived { get; init; } = true;
}

/// <summary>Which rules Autopilot acts on.</summary>
public sealed record AutopilotRules
{
	/// <summary>Only these, when non-empty.</summary>
	public IReadOnlyList<string> Include { get; init; } = [];

	/// <summary>Never these.</summary>
	public IReadOnlyList<string> Exclude { get; init; } = [];
}

/// <summary>Limits on how much Autopilot may do.</summary>
public sealed record AutopilotCaps
{
	/// <summary>Pushes per rolling hour.</summary>
	public int PushesPerHour { get; init; } = 10;

	/// <summary>Pushes per UTC day.</summary>
	public int PushesPerDay { get; init; } = 30;

	/// <summary>Publishes to nuget.org per UTC day.</summary>
	public int PublishesPerDay { get; init; } = 3;

	/// <summary>AI sessions started per rolling hour.</summary>
	public int AiSessionsPerHour { get; init; } = 12;

	/// <summary>The most files one change may touch.</summary>
	public int MaxFilesPerChange { get; init; } = 25;

	/// <summary>The most lines (added plus removed) one change may touch.</summary>
	public int MaxLinesPerChange { get; init; } = 1500;
}

/// <summary>What Autopilot may do, one switch each.</summary>
public sealed record AutopilotActions
{
	/// <summary>Commit and push at all.</summary>
	public bool Push { get; init; } = true;

	/// <summary>Push to a repository's default branch rather than a branch.</summary>
	public bool PushDefaultBranch { get; init; } = true;

	/// <summary>Publish packages to nuget.org.</summary>
	public bool Publish { get; init; } = true;

	/// <summary>Write repository secrets, such as <c>CODACY_PROJECT_TOKEN</c>.</summary>
	public bool WriteSecrets { get; init; } = true;

	/// <summary>Edit files under <c>.github/workflows</c>.</summary>
	public bool EditWorkflows { get; init; } = true;

	/// <summary>Edit <c>Publish.ps1</c>.</summary>
	public bool EditPublishScript { get; init; } = true;
}

/// <summary>What stops Autopilot starting new work.</summary>
public sealed record AutopilotBreaker
{
	/// <summary>Consecutive failed cycles that trip it.</summary>
	public int ConsecutiveFailures { get; init; } = 5;

	/// <summary>What to do when CI goes red on a commit Autopilot pushed: <c>revert+quarantine</c>, <c>quarantine</c> or <c>none</c>.</summary>
	public string CiRedAfterPush { get; init; } = "revert+quarantine";

	/// <summary>How many recent reviews to look at when judging the negative-review rate.</summary>
	public int NegativeReviewWindow { get; init; } = 20;

	/// <summary>The fraction of those reviews that may be negative before it trips.</summary>
	public double NegativeReviewThreshold { get; init; } = 0.25;
}

/// <summary>How a rule picks its model.</summary>
public sealed record AutopilotRoute
{
	/// <summary>The model, or null for the one in Ollama settings.</summary>
	public string? Model { get; init; }

	/// <summary>The wall-clock budget for one session of this rule.</summary>
	public int? BudgetMinutes { get; init; }

	/// <summary>The model to move to after repeated failure.</summary>
	public string? EscalateTo { get; init; }
}

/// <summary>Which model does which rule.</summary>
public sealed record AutopilotRouting
{
	/// <summary>The model used when a rule has no route, or null for the one in Ollama settings.</summary>
	public string? Default { get; init; }

	/// <summary>Per-rule routes, keyed by rule id.</summary>
	public IReadOnlyDictionary<string, AutopilotRoute> ByRule { get; init; } = new Dictionary<string, AutopilotRoute>();
}

/// <summary>
/// The written rules Autopilot operates under.
/// </summary>
/// <remarks>
/// A document and not a set of settings, because it is hashed into every ledger entry: an action can then
/// be tied to exactly the rules in force when it was taken, and a change to the rules is itself an entry.
/// Every default is the one the design records, and every switch that gates an irreversible action is
/// present and permitted, as decided — the safeguards are the caps and the breaker.
/// <para>
/// <see cref="Enabled"/> is false and <see cref="DryRun"/> true until a person says otherwise.
/// </para>
/// </remarks>
public sealed record AutopilotPolicy
{
	/// <summary>Whether Autopilot runs at all.</summary>
	public bool Enabled { get; init; }

	/// <summary>Whether it plans and records without doing anything.</summary>
	public bool DryRun { get; init; } = true;

	/// <summary>When it may run.</summary>
	public AutopilotSchedule Schedule { get; init; } = new();

	/// <summary>Which repositories it may touch.</summary>
	public AutopilotEligibility Eligibility { get; init; } = new();

	/// <summary>Which rules it acts on.</summary>
	public AutopilotRules Rules { get; init; } = new();

	/// <summary>How much it may do.</summary>
	public AutopilotCaps Caps { get; init; } = new();

	/// <summary>What it may do.</summary>
	public AutopilotActions Actions { get; init; } = new();

	/// <summary>What stops it.</summary>
	public AutopilotBreaker Breaker { get; init; } = new();

	/// <summary>Which model does which job.</summary>
	public AutopilotRouting Routing { get; init; } = new();

	/// <summary>
	/// Whether Autopilot may work on a repository.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="isExcluded">Whether the application excludes it from governance. An excluded repository is never active.</param>
	public bool IsRepositoryEnabled(string repositoryFullName, bool isExcluded)
		=> !isExcluded
			&& (Eligibility.RepositoryOverrides.TryGetValue(repositoryFullName, out var enabled)
				? enabled
				: Eligibility.DefaultEnabled);

	/// <summary>
	/// Whether Autopilot may act on a rule.
	/// </summary>
	/// <param name="ruleId">The rule.</param>
	public bool IsRuleEnabled(string ruleId)
		=> !Rules.Exclude.Contains(ruleId, StringComparer.OrdinalIgnoreCase)
			&& (Rules.Include.Count == 0 || Rules.Include.Contains(ruleId, StringComparer.OrdinalIgnoreCase));

	/// <summary>
	/// The route for a rule: its own, or the default.
	/// </summary>
	/// <param name="ruleId">The rule.</param>
	public AutopilotRoute RouteFor(string ruleId)
		=> Routing.ByRule.TryGetValue(ruleId, out var route)
			? route with { Model = route.Model ?? Routing.Default }
			: new AutopilotRoute { Model = Routing.Default };
}

/// <summary>
/// Holds the policy on disk, hashes it, and records every change in the ledger.
/// </summary>
/// <remarks>
/// A policy that fails to load is replaced by the safe default (disabled, dry-run) and says so in the ledger,
/// rather than by whatever half of it parsed: a damaged file must never leave Autopilot running under rules
/// nobody wrote.
/// </remarks>
public sealed class AutopilotPolicyStore
{
	private static readonly JsonSerializerOptions _json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	private static readonly JsonSerializerOptions _compact = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	private readonly Lock _gate = new();
	private readonly string _path;
	private readonly GovernanceLedger _ledger;

	private AutopilotPolicy _current;
	private string _hash;

	/// <summary>
	/// Loads the policy at a path, or the default when there is none.
	/// </summary>
	/// <param name="path">The policy file.</param>
	/// <param name="ledger">Where changes are recorded.</param>
	public AutopilotPolicyStore(string path, GovernanceLedger ledger)
	{
		_path = path;
		_ledger = ledger;
		_current = Load(out var problem);
		_hash = Hash(_current);

		if (problem is not null)
		{
			_ledger.Append("autopilot", "policy_invalid", data: new { problem, fellBackTo = "default (disabled, dry-run)" });
		}
	}

	/// <summary>The default location: beside the ledger's folder.</summary>
	public static string DefaultPath { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"PanoramicData.NugetManagement",
		"autopilot-policy.json");

	/// <summary>The policy in force.</summary>
	public AutopilotPolicy Current
	{
		get
		{
			lock (_gate)
			{
				return _current;
			}
		}
	}

	/// <summary>The hash of the policy in force, stamped on ledger entries.</summary>
	public string PolicyHash
	{
		get
		{
			lock (_gate)
			{
				return _hash;
			}
		}
	}

	/// <summary>
	/// Replaces the policy, writing it to disk and recording the change.
	/// </summary>
	/// <param name="policy">The new policy.</param>
	/// <param name="actor">Who is changing it.</param>
	/// <param name="reason">Why. Required: a change nobody explained is the thing the record exists to prevent.</param>
	/// <returns>The new hash.</returns>
	public string Update(AutopilotPolicy policy, string actor, string reason)
	{
		if (string.IsNullOrWhiteSpace(reason))
		{
			throw new ArgumentException("A policy change needs a reason.", nameof(reason));
		}

		lock (_gate)
		{
			var previous = _hash;

			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
			File.WriteAllText(_path, JsonSerializer.Serialize(policy, _json), new UTF8Encoding(false));

			_current = policy;
			_hash = Hash(policy);

			// After the swap, so the entry carries the hash of the policy it introduced.
			_ledger.Append(actor, "policy_changed", data: new { reason, previousHash = previous, newHash = _hash, policy });

			return _hash;
		}
	}

	/// <summary>
	/// The hash of a policy: SHA-256 of its compact JSON.
	/// </summary>
	/// <param name="policy">The policy.</param>
	public static string Hash(AutopilotPolicy policy)
		=> Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(policy, _compact))))[..16];

	private AutopilotPolicy Load(out string? problem)
	{
		problem = null;

		if (!File.Exists(_path))
		{
			return new AutopilotPolicy();
		}

		try
		{
			return JsonSerializer.Deserialize<AutopilotPolicy>(File.ReadAllText(_path), _json) ?? new AutopilotPolicy();
		}
		catch (JsonException ex)
		{
			problem = ex.Message;

			return new AutopilotPolicy();
		}
	}
}
