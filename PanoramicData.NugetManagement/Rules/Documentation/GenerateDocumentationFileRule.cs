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
		if (HasMsBuildProperty(dirBuildProps, "GenerateDocumentationFile", "true"))
		{
			return Task.FromResult(Pass("GenerateDocumentationFile is enabled in Directory.Build.props."));
		}

		// Check individual .csproj files
		var csprojFiles = context.FindNonTestProjectFiles().ToList();

		if (csprojFiles.Count == 0)
		{
			return Task.FromResult(Pass("No non-test .csproj files found."));
		}

		var missing = new List<string>();
		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !HasMsBuildProperty(content, "GenerateDocumentationFile", "true"))
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
					Summary = "Add `<GenerateDocumentationFile>true</GenerateDocumentationFile>` to each non-test .csproj listed",
					Detail = "Add `<GenerateDocumentationFile>true</GenerateDocumentationFile>` to a `<PropertyGroup>` in each listed project file. Do not put it in `Directory.Build.props` unless the test projects are documented too, because it would apply to them and their missing doc comments fail the build under TreatWarningsAsErrors.",
					Data = new()
					{
						["projects_missing"] = missing.ToArray(),
						// Only the projects this rule inspects: Directory.Build.props would also reach test projects, whose missing doc comments then fail the build under TreatWarningsAsErrors.
						["remediation_type"] = "ensure_csproj_property",
						["projects"] = missing.ToArray(),
						["property_name"] = "GenerateDocumentationFile",
						["property_value"] = "true"
					}
				}));
		}

		return Task.FromResult(Pass("All non-test projects have GenerateDocumentationFile enabled."));
	}
}
