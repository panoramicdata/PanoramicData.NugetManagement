namespace PanoramicData.NugetManagement.Web.Remediations.Licensing;

/// <summary>Creates or replaces LICENSE file.</summary>
public sealed class LicenseFileRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "LIC-01";
}
