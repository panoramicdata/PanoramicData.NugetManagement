namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>Updates packages with major-level NuGet updates available.</summary>
public sealed class NuGetMajorLevelUpdatesRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "PKG-07";
}
