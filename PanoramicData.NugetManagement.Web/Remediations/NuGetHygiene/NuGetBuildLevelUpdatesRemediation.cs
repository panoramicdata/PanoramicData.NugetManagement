namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>Updates packages with build-level NuGet updates available.</summary>
public sealed class NuGetBuildLevelUpdatesRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "PKG-05";
}
