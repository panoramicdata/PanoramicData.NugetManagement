using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that SECURITY.md exists.
/// </summary>
public class SecurityMdExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "COM-01";

	/// <inheritdoc />
	public override string RuleName => "SECURITY.md exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CommunityHealth;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
		=> Task.FromResult(context.FileExists("SECURITY.md")
			? Pass("SECURITY.md found.")
			: Fail(
				"SECURITY.md not found at repository root.",
				"Create SECURITY.md with the standard security policy content."));
}

/// <summary>
/// Checks that CONTRIBUTING.md exists.
/// </summary>
public class ContributingMdExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "COM-02";

	/// <inheritdoc />
	public override string RuleName => "CONTRIBUTING.md exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CommunityHealth;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
		=> Task.FromResult(context.FileExists("CONTRIBUTING.md")
			? Pass("CONTRIBUTING.md found.")
			: Fail(
				"CONTRIBUTING.md not found at repository root.",
				"Create CONTRIBUTING.md with the standard contributing guide content."));
}

/// <summary>
/// Checks that .github/dependabot.yml exists.
/// </summary>
public class DependabotConfiguredRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "COM-03";

	/// <inheritdoc />
	public override string RuleName => "Dependabot configured";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.DependencyAutomation;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var hasDependabot = context.FileExists(".github/dependabot.yml") ||
							context.FileExists(".github/dependabot.yaml");
		var hasRenovate = context.FileExists("renovate.json") ||
						  context.FileExists(".github/renovate.json");

		return Task.FromResult(hasDependabot || hasRenovate
			? Pass("Dependency update automation configured (Dependabot or Renovate).")
			: Fail(
				"No dependency update automation found (Dependabot or Renovate).",
				"Create .github/dependabot.yml with NuGet and GitHub Actions ecosystems."));
	}
}

/// <summary>
/// Checks that a CodeQL / SAST workflow exists.
/// </summary>
public class CodeQlWorkflowRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "COM-04";

	/// <inheritdoc />
	public override string RuleName => "CodeQL / SAST workflow exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var workflowFiles = context.FilePaths
			.Where(p => p.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase))
			.ToList();

		foreach (var wf in workflowFiles)
		{
			var content = context.GetFileContent(wf);
			if (Contains(content, "codeql") || Contains(content, "CodeQL"))
			{
				return Task.FromResult(Pass("CodeQL workflow found."));
			}
		}

		return Task.FromResult(Fail(
			"No CodeQL / SAST workflow found.",
			"Add a GitHub Actions workflow using github/codeql-action for static analysis."));
	}
}
