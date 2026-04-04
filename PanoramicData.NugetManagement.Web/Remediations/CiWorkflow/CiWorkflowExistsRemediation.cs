namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Creates CI workflow file from template.</summary>
public sealed class CiWorkflowExistsRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "CI-01";
}
