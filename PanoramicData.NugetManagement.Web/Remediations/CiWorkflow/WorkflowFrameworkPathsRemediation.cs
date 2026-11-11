namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Points a workflow's build-output paths at the framework the repository now targets.</summary>
public sealed class WorkflowFrameworkPathsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CI-17";
}
