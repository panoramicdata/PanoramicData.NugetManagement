using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>
/// Marks a remediation that no rule owns, so <see cref="RemediationRegistry"/> leaves it out.
/// </summary>
/// <remarks>
/// The registry discovers every <see cref="IRemediation"/> in the assembly by reflection and keys
/// them by <see cref="IRemediation.RuleId"/>. That is right for a remediation that fixes a rule, and
/// wrong for one whose source is something else — Dependabot adoption applies what a pull request
/// proposed, and has no rule behind it. Registered, it would put a rule id that is not a rule into
/// <see cref="RemediationRegistry.RegisteredRuleIds"/>, where anything comparing the registry against
/// the rule set has to special-case it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class UnregisteredRemediationAttribute : Attribute;

/// <summary>
/// Defines a file-system remediation for a specific governance rule.
/// </summary>
public interface IRemediation
{
	/// <summary>
	/// The rule ID this remediation handles (e.g. "BLD-01").
	/// </summary>
	string RuleId { get; }

	/// <summary>
	/// Determines whether this remediation can be applied given the rule result.
	/// </summary>
	bool CanRemediate(RuleResult result);

	/// <summary>
	/// Applies the remediation to the local filesystem.
	/// </summary>
	/// <param name="localPath">The root path of the cloned repository.</param>
	/// <param name="result">The failed rule result with advisory data.</param>
	/// <param name="applied">List to append successfully modified file paths to.</param>
	/// <param name="onOutput">Optional callback for progress messages.</param>
	void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput);
}
