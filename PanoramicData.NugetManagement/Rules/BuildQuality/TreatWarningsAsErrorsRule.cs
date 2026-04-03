using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that TreatWarningsAsErrors is enabled.
/// </summary>
public class TreatWarningsAsErrorsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "BLD-01";

	/// <inheritdoc />
	public override string RuleName => "TreatWarningsAsErrors enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.BuildQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirBuild = context.GetFileContent("Directory.Build.props");
		if (Contains(dirBuild, "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>"))
		{
			return Task.FromResult(Pass("TreatWarningsAsErrors is enabled in Directory.Build.props."));
		}

		return Task.FromResult(Fail(
			"TreatWarningsAsErrors is not enabled in Directory.Build.props.",
			new RuleAdvisory
			{
				Summary = "Enable `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in Directory.Build.props",
				Detail = "Add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` to a `<PropertyGroup>` in `Directory.Build.props`. This ensures all compiler warnings are treated as errors, preventing warning accumulation.",
				Data = new()
				{
					["file"] = "Directory.Build.props",
					["remediation_type"] = "ensure_xml_property",
					["property_name"] = "TreatWarningsAsErrors",
					["property_value"] = "true"
				}
			}));
	}
}
