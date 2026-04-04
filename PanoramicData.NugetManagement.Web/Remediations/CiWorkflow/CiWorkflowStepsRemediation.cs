namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Fixes CI workflow steps configuration.</summary>
public sealed class CiWorkflowStepsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CI-03";
}
