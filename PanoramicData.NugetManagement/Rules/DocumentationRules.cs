using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that GenerateDocumentationFile is enabled for non-test projects.
/// </summary>
public class GenerateDocumentationFileRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "DOC-01";

	/// <inheritdoc />
	public override string RuleName => "GenerateDocumentationFile enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Documentation;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		// Check Directory.Build.props first
		var dirBuildProps = context.GetFileContent("Directory.Build.props");
		if (Contains(dirBuildProps, "<GenerateDocumentationFile>true</GenerateDocumentationFile>"))
		{
			return Task.FromResult(Pass("GenerateDocumentationFile is enabled in Directory.Build.props."));
		}

		// Check individual .csproj files
		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase))
			.ToList();

		if (csprojFiles.Count == 0)
		{
			return Task.FromResult(Pass("No non-test .csproj files found."));
		}

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<GenerateDocumentationFile>true</GenerateDocumentationFile>"))
			{
				return Task.FromResult(Fail(
					$"{csproj} does not have GenerateDocumentationFile enabled.",
					"Add <GenerateDocumentationFile>true</GenerateDocumentationFile> to the .csproj or Directory.Build.props."));
			}
		}

		return Task.FromResult(Pass("All non-test projects have GenerateDocumentationFile enabled."));
	}
}
