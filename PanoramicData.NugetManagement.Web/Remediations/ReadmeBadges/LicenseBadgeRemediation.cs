namespace PanoramicData.NugetManagement.Web.Remediations.ReadmeBadges;

/// <summary>Prepends license badge to README.md.</summary>
public sealed class LicenseBadgeRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "README-04";
}
