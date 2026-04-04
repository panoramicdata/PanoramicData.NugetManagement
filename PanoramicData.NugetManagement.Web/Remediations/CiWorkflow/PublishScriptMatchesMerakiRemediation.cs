namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Fixes publish script to match expected content.</summary>
public sealed class PublishScriptMatchesMerakiRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CI-09";
}
