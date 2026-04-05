namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Deletes nuget-key.txt from the repository.</summary>
public sealed class NugetKeyNotCommittedRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CI-10";
}
