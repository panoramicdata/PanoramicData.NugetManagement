namespace PanoramicData.NugetManagement.Web.Remediations.ProjectMetadata;

/// <summary>Rewrites a declared GitHub URL to the repository's own name.</summary>
public sealed class DeclaredRepositoryUrlRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "META-06";
}
