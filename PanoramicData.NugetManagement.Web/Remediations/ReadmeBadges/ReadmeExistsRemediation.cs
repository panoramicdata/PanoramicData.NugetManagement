namespace PanoramicData.NugetManagement.Web.Remediations.ReadmeBadges;

/// <summary>Creates README.md from template.</summary>
public sealed class ReadmeExistsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "README-01";
}
