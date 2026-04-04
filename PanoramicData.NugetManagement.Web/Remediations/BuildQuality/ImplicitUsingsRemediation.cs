namespace PanoramicData.NugetManagement.Web.Remediations.BuildQuality;

/// <summary>Adds ImplicitUsings enable to Directory.Build.props.</summary>
public sealed class ImplicitUsingsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "BLD-03";
}
