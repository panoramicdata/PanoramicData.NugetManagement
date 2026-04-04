using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations;

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
