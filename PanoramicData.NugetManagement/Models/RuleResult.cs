namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The result of evaluating a single assessment rule against a repository.
/// </summary>
public class RuleResult
{
	/// <summary>
	/// The unique identifier of the rule (e.g. "CI-01").
	/// </summary>
	public required string RuleId { get; init; }

	/// <summary>
	/// Human-readable name of the rule.
	/// </summary>
	public required string RuleName { get; init; }

	/// <summary>
	/// The category this rule belongs to.
	/// </summary>
	public required AssessmentCategory Category { get; init; }

	/// <summary>
	/// The severity of a violation of this rule.
	/// </summary>
	public required AssessmentSeverity Severity { get; init; }

	/// <summary>
	/// Whether the repository passes this rule.
	/// </summary>
	public required bool Passed { get; init; }

	/// <summary>
	/// A human-readable message explaining the result.
	/// </summary>
	public required string Message { get; init; }

	/// <summary>
	/// Optional details providing remediation guidance.
	/// </summary>
	public string? Remediation { get; init; }
}
