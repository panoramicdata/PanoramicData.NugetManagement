namespace PanoramicData.NugetManagement.Web.Remediations.ReadmeBadges;

/// <summary>Prepends Codacy badge to README.md.</summary>
public sealed class CodacyBadgeRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "README-02";
}
