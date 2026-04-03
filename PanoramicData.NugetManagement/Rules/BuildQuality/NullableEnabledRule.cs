using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that nullable reference types are enabled.
/// </summary>
public class NullableEnabledRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "BLD-02";

	/// <inheritdoc />
	public override string RuleName => "Nullable enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.BuildQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirBuild = context.GetFileContent("Directory.Build.props");
		if (Contains(dirBuild, "<Nullable>enable</Nullable>"))
		{
			return Task.FromResult(Pass("Nullable is enabled in Directory.Build.props."));
		}

		return Task.FromResult(Fail(
			"Nullable reference types are not enabled in Directory.Build.props.",
			new RuleAdvisory
			{
				Summary = "Enable `<Nullable>enable</Nullable>` in Directory.Build.props",
				Detail = "Add `<Nullable>enable</Nullable>` to a `<PropertyGroup>` in `Directory.Build.props`. This enables nullable reference type annotations and warnings across all projects.",
				Data = new()
				{
					["file"] = "Directory.Build.props",
					["remediation_type"] = "ensure_xml_property",
					["property_name"] = "Nullable",
					["property_value"] = "enable"
				}
			}));
	}
}
