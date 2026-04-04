namespace PanoramicData.NugetManagement.Web.Remediations.ProjectMetadata;

/// <summary>Adds RepositoryUrl to Directory.Build.props.</summary>
public sealed class RepositoryUrlSetRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "META-02";
}
