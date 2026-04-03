using PanoramicData.NugetManagement.Models;

#pragma warning disable CS0618 // Obsolete Fail overload used by some code paths

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
				new RuleAdvisory
				{
					Summary = "Create SECURITY.md with the standard security policy content",
					Detail = "Create a `SECURITY.md` file at the repository root with the standard security policy.",
					Data = new()
					{
						["expected_path"] = "SECURITY.md",
						["template_content"] = Standards.SecurityMdContent
					}
				}));
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
				new RuleAdvisory
				{
					Summary = "Create CONTRIBUTING.md with the standard contributing guide",
					Detail = "Create a `CONTRIBUTING.md` file at the repository root with the standard contributing guide.",
					Data = new()
					{
						["expected_path"] = "CONTRIBUTING.md",
						["template_content"] = Standards.ContributingMdContent
					}
				}));
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
				new RuleAdvisory
				{
					Summary = "Create `.github/dependabot.yml` with NuGet and GitHub Actions ecosystems",
					Detail = "Create a `.github/dependabot.yml` file configuring automatic dependency updates for both NuGet packages and GitHub Actions.",
					Data = new()
					{
						["expected_path"] = ".github/dependabot.yml",
						["template_content"] = Standards.DependabotYmlContent
					}
				}));
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
			new RuleAdvisory
			{
				Summary = "Add a GitHub Actions workflow using `github/codeql-action` for static analysis",
				Detail = "Add a GitHub Actions workflow (e.g. `.github/workflows/codeql.yml`) that runs `github/codeql-action` for static analysis on push and pull request.",
				Data = new()
				{
					["expected_path"] = ".github/workflows/codeql.yml",
					["template_content"] = Standards.CodeQlWorkflowContent
				}
			}));
	}
}
