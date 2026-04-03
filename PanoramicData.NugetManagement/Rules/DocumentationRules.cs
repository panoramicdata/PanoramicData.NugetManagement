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

		var missing = new List<string>();
		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<GenerateDocumentationFile>true</GenerateDocumentationFile>"))
			{
				missing.Add(csproj);
			}
		}

		if (missing.Count > 0)
		{
			return Task.FromResult(Fail(
				$"The following projects do not have GenerateDocumentationFile enabled: {string.Join(", ", missing)}",
				new RuleAdvisory
				{
					Summary = "Add `<GenerateDocumentationFile>true</GenerateDocumentationFile>` to Directory.Build.props or each non-test .csproj",
					Detail = "Add `<GenerateDocumentationFile>true</GenerateDocumentationFile>` to a `<PropertyGroup>` in `Directory.Build.props` (preferred) or each listed project file.",
					Data = new() { ["projects_missing"] = missing.ToArray() }
				}));
		}

		return Task.FromResult(Pass("All non-test projects have GenerateDocumentationFile enabled."));
	}
}
