namespace PanoramicData.NugetManagement.Web.Remediations.ProjectMetadata;

/// <summary>Adds Authors and Company to Directory.Build.props.</summary>
public sealed class AuthorsAndCompanyRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "META-03";
}
