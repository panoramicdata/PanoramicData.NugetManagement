namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>Removes PackageReferences the target framework already supplies.</summary>
public sealed class PrunedPackageReferenceRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "PKG-15";
}
