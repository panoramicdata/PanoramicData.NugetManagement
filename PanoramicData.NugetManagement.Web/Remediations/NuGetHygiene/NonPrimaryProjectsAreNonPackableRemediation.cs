namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>
/// Adds <IsPackable>false</IsPackable> to non-primary, non-cli ancillary projects.
/// </summary>
public sealed class NonPrimaryProjectsAreNonPackableRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "PKG-09";
}
