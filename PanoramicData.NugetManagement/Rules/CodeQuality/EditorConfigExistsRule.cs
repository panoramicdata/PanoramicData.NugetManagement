using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that .editorconfig exists at the repository root.
/// </summary>
public class EditorConfigExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CQ-01";

	/// <inheritdoc />
	public override string RuleName => ".editorconfig exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent(".editorconfig");
		if (content is null)
		{
			return Task.FromResult(Fail(
				".editorconfig not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create an .editorconfig file with root = true and standard C# formatting rules.",
					Detail = "Create a `.editorconfig` file at the repository root with `root = true` at the top, followed by standard C# formatting rules.",
					Data = new()
					{
						["expected_path"] = ".editorconfig",
						["template_content"] = Standards.EditorConfigContent
					}
				}));
		}

		return Task.FromResult(Contains(content, "root = true")
			? Pass(".editorconfig found with root = true.")
			: Fail(
				".editorconfig does not contain 'root = true'.",
				new RuleAdvisory
				{
					Summary = "Add 'root = true' at the top of .editorconfig.",
					Detail = "The `.editorconfig` file exists but is missing `root = true`. Add this as the first non-comment line to prevent editors from searching parent directories.",
					Data = new()
					{
						["file"] = ".editorconfig",
						["remediation_type"] = "prepend_line",
						["line_content"] = "root = true"
					}
				}));
	}
}
