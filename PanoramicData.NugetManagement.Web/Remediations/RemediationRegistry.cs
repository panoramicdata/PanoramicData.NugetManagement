using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>
/// Registry of all available remediations, keyed by rule ID.
/// </summary>
public sealed class RemediationRegistry
{
	private readonly Dictionary<string, IRemediation> _remediations;

	/// <summary>
	/// Initializes a new instance of the <see cref="RemediationRegistry"/> class,
	/// discovering all <see cref="IRemediation"/> implementations.
	/// </summary>
	public RemediationRegistry()
	{
		_remediations = typeof(RemediationRegistry).Assembly
			.GetTypes()
			.Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IRemediation).IsAssignableFrom(t))
			.Select(t => (IRemediation)Activator.CreateInstance(t)!)
			.ToDictionary(r => r.RuleId, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Gets the remediation for a given rule ID, or null if none exists.
	/// </summary>
	public IRemediation? Get(string ruleId)
		=> _remediations.TryGetValue(ruleId, out var remediation) ? remediation : null;

	/// <summary>
	/// Checks whether a remediation exists for the given rule result and can be applied.
	/// </summary>
	public bool CanRemediate(RuleResult result)
	{
		if (result.Passed || result.Advisory is null)
		{
			return false;
		}

		var remediation = Get(result.RuleId);
		return remediation?.CanRemediate(result) == true;
	}

	/// <summary>
	/// Gets all registered rule IDs that have remediations.
	/// </summary>
	public IReadOnlyCollection<string> RegisteredRuleIds => _remediations.Keys;
}
