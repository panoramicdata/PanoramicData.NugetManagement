namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Fixes CI workflow to match expected content.</summary>
public sealed class CiWorkflowMatchesMerakiRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "CI-08";
}
