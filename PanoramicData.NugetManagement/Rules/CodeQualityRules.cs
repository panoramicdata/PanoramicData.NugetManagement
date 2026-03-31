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
				"Create an .editorconfig file with root = true and standard C# formatting rules."));
		}

		return Task.FromResult(Contains(content, "root = true")
			? Pass(".editorconfig found with root = true.")
			: Fail(
				".editorconfig does not contain 'root = true'.",
				"Add 'root = true' at the top of .editorconfig."));
	}
}

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
				"Create an .editorconfig file and set csharp_style_namespace_declarations = file_scoped:error."));
		}

		return Task.FromResult(Contains(content, "csharp_style_namespace_declarations = file_scoped")
			? Pass("File-scoped namespaces are enforced in .editorconfig.")
			: Fail(
				".editorconfig does not enforce file-scoped namespaces.",
				"Add 'csharp_style_namespace_declarations = file_scoped:error' to .editorconfig."));
	}
}

/// <summary>
/// Checks that Codacy is configured (.codacy directory or integration).
/// </summary>
public class CodacyConfiguredRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CQ-03";

	/// <inheritdoc />
	public override string RuleName => "Codacy configured";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var hasCodacyDir = context.FilePaths.Any(p =>
			p.StartsWith(".codacy/", StringComparison.OrdinalIgnoreCase));
		var hasCodacyYml = context.FileExists(".codacy.yml") || context.FileExists(".codacy.yaml");

		return Task.FromResult(hasCodacyDir || hasCodacyYml
			? Pass("Codacy is configured.")
			: Fail(
				"No Codacy configuration found (.codacy/ directory or .codacy.yml).",
				"Set up Codacy integration and add .codacy/cli.sh or .codacy.yml."));
	}
}
