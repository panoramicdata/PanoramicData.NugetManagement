namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Creates publish script from template.</summary>
public sealed class PublishScriptExistsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CI-07";
}
