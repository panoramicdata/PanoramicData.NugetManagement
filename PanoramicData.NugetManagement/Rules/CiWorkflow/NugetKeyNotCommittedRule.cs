using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that nuget-key.txt is not committed to the repository.
/// The .gitignore entry should prevent this, but if the file was committed
/// before the .gitignore rule was added, it needs to be removed.
/// </summary>
public class NugetKeyNotCommittedRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-10";

	/// <inheritdoc />
	public override string RuleName => "nuget-key.txt not committed";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
		=> Task.FromResult(context.FileExists("nuget-key.txt")
			? Fail(
				"nuget-key.txt is committed to the repository — risk of leaking NuGet API key.",
				new RuleAdvisory
				{
					Summary = "Delete nuget-key.txt from the repository.",
					Detail = "The file `nuget-key.txt` is committed to the repository. This file may contain a NuGet API key and should be deleted. Ensure `.gitignore` includes `nuget-key.txt` to prevent it being re-committed.",
					Data = new()
					{
						["remediation_type"] = "delete_file",
						["file"] = "nuget-key.txt"
					}
				})
			: Pass("nuget-key.txt is not committed to the repository."));
}
