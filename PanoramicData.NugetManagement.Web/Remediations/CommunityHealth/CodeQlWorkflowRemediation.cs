namespace PanoramicData.NugetManagement.Web.Remediations.CommunityHealth;

/// <summary>Creates CodeQL workflow from template.</summary>
public sealed class CodeQlWorkflowRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "COM-04";
}
