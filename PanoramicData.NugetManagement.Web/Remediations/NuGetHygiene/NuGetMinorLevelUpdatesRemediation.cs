namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>Updates packages with minor-level NuGet updates available.</summary>
public sealed class NuGetMinorLevelUpdatesRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "PKG-06";
}
