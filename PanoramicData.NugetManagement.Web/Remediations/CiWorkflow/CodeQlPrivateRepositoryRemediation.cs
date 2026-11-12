namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Skips the CodeQL job in a private repository, where uploading its results needs Advanced Security.</summary>
public sealed class CodeQlPrivateRepositoryRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CI-18";
}
