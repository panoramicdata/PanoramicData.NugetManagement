using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Interface for a rule that assesses a repository against a best practice.
/// </summary>
public interface IRule
{
	/// <summary>
	/// The unique identifier of the rule (e.g. "CI-01").
	/// </summary>
	string RuleId { get; }

	/// <summary>
	/// Human-readable name of the rule.
	/// </summary>
	string RuleName { get; }

	/// <summary>
	/// The category this rule belongs to.
	/// </summary>
	AssessmentCategory Category { get; }

	/// <summary>
	/// The severity of a violation of this rule.
	/// </summary>
	AssessmentSeverity Severity { get; }

	/// <summary>
	/// Evaluates the rule against the given repository context.
	/// </summary>
	/// <param name="context">The repository context to evaluate.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The result of evaluating this rule.</returns>
	Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken);
}
