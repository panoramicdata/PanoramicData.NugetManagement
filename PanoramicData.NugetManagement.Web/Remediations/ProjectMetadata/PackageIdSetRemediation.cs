namespace PanoramicData.NugetManagement.Web.Remediations.ProjectMetadata;

/// <summary>Adds PackageId to Directory.Build.props.</summary>
public sealed class PackageIdSetRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "META-01";
}
