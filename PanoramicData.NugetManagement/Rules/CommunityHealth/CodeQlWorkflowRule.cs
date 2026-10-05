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
				return Task.FromResult(EvaluateWorkflow(context, wf, content!));
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

	/// <summary>
	/// A CodeQL workflow that builds a Nerdbank.GitVersioning repository from a shallow clone fails on
	/// every run: the build cannot calculate the version height, so CodeQL reports "unable to
	/// automatically build your code" and the repository silently has no static analysis. Presence of the
	/// workflow is therefore not enough; it must also check out the full history.
	/// </summary>
	private RuleResult EvaluateWorkflow(RepositoryContext context, string workflowPath, string content)
	{
		if (!context.FileExists("version.json") || Contains(content, "fetch-depth: 0"))
		{
			return Pass("CodeQL workflow found.");
		}

		var data = new Dictionary<string, object> { ["workflow_file"] = workflowPath };

		// Setting the key on a checkout step that is already there is mechanical. Adding the step
		// itself is not, so that case is left without a mechanical fix.
		if (Contains(content, "actions/checkout@"))
		{
			data["remediation_type"] = "ensure_checkout_fetch_depth";
			data["file"] = workflowPath;
		}

		return Fail(
			"CodeQL workflow checks out a shallow clone, which breaks the build of a Nerdbank.GitVersioning repository.",
			new RuleAdvisory
			{
				Summary = "Set `fetch-depth: 0` on actions/checkout in the CodeQL workflow",
				Detail = "Add `with: fetch-depth: 0` to the `actions/checkout` step in the CodeQL workflow. Without it Nerdbank.GitVersioning cannot calculate the version, the autobuild step fails and CodeQL never analyses the code.",
				Data = data
			});
	}
}
