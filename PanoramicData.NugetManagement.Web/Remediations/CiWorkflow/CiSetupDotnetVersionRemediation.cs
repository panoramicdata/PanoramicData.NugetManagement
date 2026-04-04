namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Fixes setup-dotnet version in CI workflow.</summary>
public sealed class CiSetupDotnetVersionRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CI-06";
}
