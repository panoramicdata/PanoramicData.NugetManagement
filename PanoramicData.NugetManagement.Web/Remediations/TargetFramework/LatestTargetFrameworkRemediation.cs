namespace PanoramicData.NugetManagement.Web.Remediations.TargetFramework;

/// <summary>Updates TargetFramework to latest .NET version.</summary>
public sealed class LatestTargetFrameworkRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "TFM-01";
}
