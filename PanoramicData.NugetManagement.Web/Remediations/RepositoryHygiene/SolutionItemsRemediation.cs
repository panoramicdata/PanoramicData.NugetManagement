namespace PanoramicData.NugetManagement.Web.Remediations.RepositoryHygiene;

/// <summary>Adds missing file entries to Solution Items in .slnx.</summary>
public sealed class SolutionItemsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "REPO-05";
}
