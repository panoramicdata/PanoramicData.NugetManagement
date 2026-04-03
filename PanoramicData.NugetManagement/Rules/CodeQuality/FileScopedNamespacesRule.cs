using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that file-scoped namespaces are enforced in .editorconfig.
/// </summary>
public class FileScopedNamespacesRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CQ-02";

	/// <inheritdoc />
	public override string RuleName => "File-scoped namespaces enforced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent(".editorconfig");
		if (content is null)
		{
			return Task.FromResult(Fail(
				".editorconfig not found.",
				new RuleAdvisory
				{
					Summary = "Create an .editorconfig file and set csharp_style_namespace_declarations = file_scoped:error.",
					Detail = "Create a `.editorconfig` file at the repository root and add `csharp_style_namespace_declarations = file_scoped:error` in the `[*.cs]` section.",
					Data = new()
					{
						["file"] = ".editorconfig",
						["remediation_type"] = "append_line",
						["line_content"] = "csharp_style_namespace_declarations = file_scoped:error"
					}
				}));
		}

		return Task.FromResult(Contains(content, "csharp_style_namespace_declarations = file_scoped")
			? Pass("File-scoped namespaces are enforced in .editorconfig.")
			: Fail(
				".editorconfig does not enforce file-scoped namespaces.",
				new RuleAdvisory
				{
					Summary = "Add 'csharp_style_namespace_declarations = file_scoped:error' to .editorconfig.",
					Detail = "The `.editorconfig` file does not enforce file-scoped namespaces. Add `csharp_style_namespace_declarations = file_scoped:error` to the `[*.cs]` section.",
					Data = new()
					{
						["file"] = ".editorconfig",
						["remediation_type"] = "append_line",
						["line_content"] = "csharp_style_namespace_declarations = file_scoped:error"
					}
				}));
	}
}
