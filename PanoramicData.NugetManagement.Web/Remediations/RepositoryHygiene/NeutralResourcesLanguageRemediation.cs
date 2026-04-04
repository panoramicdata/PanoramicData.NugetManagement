namespace PanoramicData.NugetManagement.Web.Remediations.RepositoryHygiene;

/// <summary>Adds NeutralResourcesLanguage to Directory.Build.props.</summary>
public sealed class NeutralResourcesLanguageRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "REPO-03";
}
