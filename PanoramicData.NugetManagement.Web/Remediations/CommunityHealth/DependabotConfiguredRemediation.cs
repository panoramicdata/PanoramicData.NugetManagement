namespace PanoramicData.NugetManagement.Web.Remediations.CommunityHealth;

/// <summary>Creates dependabot.yml from template.</summary>
public sealed class DependabotConfiguredRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "COM-03";
}
