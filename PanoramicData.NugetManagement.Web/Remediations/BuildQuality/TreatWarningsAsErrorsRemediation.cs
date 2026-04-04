namespace PanoramicData.NugetManagement.Web.Remediations.BuildQuality;

/// <summary>Adds TreatWarningsAsErrors to Directory.Build.props.</summary>
public sealed class TreatWarningsAsErrorsRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "BLD-01";
}
