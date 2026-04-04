namespace PanoramicData.NugetManagement.Web.Remediations.CentralPackageManagement;

/// <summary>Enables Central Package Management in Directory.Build.props.</summary>
public sealed class CpmEnabledRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CPM-01";
}
