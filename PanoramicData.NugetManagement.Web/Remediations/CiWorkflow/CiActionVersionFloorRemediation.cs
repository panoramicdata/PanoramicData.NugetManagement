namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Rewrites every workflow's `uses:` line for each action CI-12 found behind.</summary>
public sealed class CiActionVersionFloorRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CI-12";
}
