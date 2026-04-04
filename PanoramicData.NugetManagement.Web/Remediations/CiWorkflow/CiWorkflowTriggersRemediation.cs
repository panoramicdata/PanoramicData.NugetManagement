namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Fixes CI workflow triggers configuration.</summary>
public sealed class CiWorkflowTriggersRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CI-02";
}
