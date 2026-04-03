using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that NuGetAuditMode is set to All.
/// </summary>
public class NuGetAuditModeRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-04";

	/// <inheritdoc />
	public override string RuleName => "NuGetAuditMode = All";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirBuild = context.GetFileContent("Directory.Build.props");
		if (Contains(dirBuild, "<NuGetAuditMode>All</NuGetAuditMode>"))
		{
			return Task.FromResult(Pass("NuGetAuditMode is set to All in Directory.Build.props."));
		}

		// Check individual csproj files
		var csprojFiles = context.FindFiles(".csproj");
		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (Contains(content, "<NuGetAuditMode>All</NuGetAuditMode>"))
			{
				return Task.FromResult(Pass($"NuGetAuditMode is set to All in {csproj}."));
			}
		}

		return Task.FromResult(Fail(
			"NuGetAuditMode is not set to All.",
			new RuleAdvisory
			{
				Summary = "Add <NuGetAuditMode>All</NuGetAuditMode> to Directory.Build.props.",
				Detail = "No project or `Directory.Build.props` sets `NuGetAuditMode` to `All`. Add `<NuGetAuditMode>All</NuGetAuditMode>` to `Directory.Build.props` to enable transitive NuGet vulnerability auditing.",
				Data = new()
				{
					["file"] = "Directory.Build.props",
					["remediation_type"] = "ensure_xml_property",
					["property_name"] = "NuGetAuditMode",
					["property_value"] = "All"
				}
			}));
	}
}
