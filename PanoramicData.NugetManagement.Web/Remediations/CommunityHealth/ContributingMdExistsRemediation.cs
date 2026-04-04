namespace PanoramicData.NugetManagement.Web.Remediations.CommunityHealth;

/// <summary>Creates CONTRIBUTING.md from template.</summary>
public sealed class ContributingMdExistsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "COM-02";
}
