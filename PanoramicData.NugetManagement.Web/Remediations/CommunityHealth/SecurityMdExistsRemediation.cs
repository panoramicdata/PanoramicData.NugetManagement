namespace PanoramicData.NugetManagement.Web.Remediations.CommunityHealth;

/// <summary>Creates SECURITY.md from template.</summary>
public sealed class SecurityMdExistsRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "COM-01";
}
