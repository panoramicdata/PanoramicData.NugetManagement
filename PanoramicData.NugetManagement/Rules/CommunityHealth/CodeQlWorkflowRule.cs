using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

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
