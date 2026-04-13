using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Base class for rules, providing common helper methods.
/// </summary>
public abstract class RuleBase : IRule
{
	/// <inheritdoc />
	public abstract string RuleId { get; }

	/// <inheritdoc />
	public abstract string RuleName { get; }

	/// <inheritdoc />
	public abstract AssessmentCategory Category { get; }

	/// <inheritdoc />
	public abstract AssessmentSeverity Severity { get; }

	/// <inheritdoc />
	public abstract Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken);

	/// <summary>
	/// Creates a passing result for this rule.
	/// </summary>
	/// <param name="message">The success message.</param>
	/// <returns>A passing RuleResult.</returns>
	protected RuleResult Pass(string message) => new()
	{
		RuleId = RuleId,
		RuleName = RuleName,
		Category = Category,
		Severity = Severity,
		Passed = true,
		Message = message
	};

	/// <summary>
	/// Creates a failing result for this rule with structured advisory data.
	/// </summary>
	/// <param name="message">The failure message.</param>
	/// <param name="advisory">Structured advisory for AI-driven remediation.</param>
	/// <returns>A failing RuleResult.</returns>
	protected RuleResult Fail(string message, RuleAdvisory advisory) => new()
	{
		RuleId = RuleId,
		RuleName = RuleName,
		Category = Category,
		Severity = Severity,
		Passed = false,
		Message = message,
#pragma warning disable CS0618 // Type or member is obsolete
		Remediation = advisory.Summary,
#pragma warning restore CS0618 // Type or member is obsolete
		Advisory = advisory
	};

	/// <summary>
	/// Creates a failing result for this rule.
	/// </summary>
	/// <param name="message">The failure message.</param>
	/// <param name="remediation">Optional remediation guidance.</param>
	/// <returns>A failing RuleResult.</returns>
	[Obsolete("Use Fail(string, RuleAdvisory) instead.")]
	protected RuleResult Fail(string message, string? remediation = null) => new()
	{
		RuleId = RuleId,
		RuleName = RuleName,
		Category = Category,
		Severity = Severity,
		Passed = false,
		Message = message,
#pragma warning disable CS0618 // Type or member is obsolete
		Remediation = remediation
#pragma warning restore CS0618 // Type or member is obsolete
	};

	/// <summary>
	/// Checks whether a file content contains a specific string.
	/// </summary>
	/// <param name="content">The file content to search.</param>
	/// <param name="search">The string to search for.</param>
	/// <returns>True if found.</returns>
	protected static bool Contains(string? content, string search)
		=> content?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

	/// <summary>
	/// Checks whether a project file is explicitly marked as non-packable.
	/// </summary>
	/// <param name="content">The .csproj file content.</param>
	/// <returns>True if the project contains &lt;IsPackable&gt;false&lt;/IsPackable&gt;.</returns>
	protected static bool IsExplicitlyNonPackable(string? content)
		=> Contains(content, "<IsPackable>false</IsPackable>");
}
