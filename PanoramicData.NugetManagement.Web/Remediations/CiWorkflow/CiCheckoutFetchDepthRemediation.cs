namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Fixes checkout fetch-depth in CI workflow.</summary>
public sealed class CiCheckoutFetchDepthRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CI-04";
}
