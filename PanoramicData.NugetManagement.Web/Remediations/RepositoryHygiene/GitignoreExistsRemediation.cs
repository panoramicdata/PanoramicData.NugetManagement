namespace PanoramicData.NugetManagement.Web.Remediations.RepositoryHygiene;

/// <summary>Creates .gitignore from template.</summary>
public sealed class GitignoreExistsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "REPO-01";
}
