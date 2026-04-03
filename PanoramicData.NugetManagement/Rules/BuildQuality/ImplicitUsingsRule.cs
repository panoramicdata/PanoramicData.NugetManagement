using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that ImplicitUsings is enabled.
/// </summary>
public class ImplicitUsingsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "BLD-03";

	/// <inheritdoc />
	public override string RuleName => "ImplicitUsings enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.BuildQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var csprojFiles = context.FindFiles(".csproj").ToList();
		var missing = new List<string>();

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<ImplicitUsings>enable</ImplicitUsings>"))
			{
				missing.Add(csproj);
			}
		}

		return Task.FromResult(missing.Count == 0
			? Pass("All projects have ImplicitUsings enabled.")
			: Fail(
				$"The following projects do not have ImplicitUsings enabled: {string.Join(", ", missing)}",
				new RuleAdvisory
				{
					Summary = "Enable `<ImplicitUsings>enable</ImplicitUsings>` in each .csproj",
					Detail = "Add `<ImplicitUsings>enable</ImplicitUsings>` to the `<PropertyGroup>` in each project file listed below.",
					Data = new()
					{
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "ImplicitUsings",
						["property_value"] = "enable",
						["projects"] = missing.ToArray()
					}
				}));
	}
}
