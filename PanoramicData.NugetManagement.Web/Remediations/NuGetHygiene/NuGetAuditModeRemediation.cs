namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>Adds NuGetAuditMode to Directory.Build.props.</summary>
public sealed class NuGetAuditModeRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "PKG-04";
}
