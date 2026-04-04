namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>Adds GeneratePackageOnBuild to Directory.Build.props.</summary>
public sealed class GeneratePackageOnBuildRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "PKG-02";
}
