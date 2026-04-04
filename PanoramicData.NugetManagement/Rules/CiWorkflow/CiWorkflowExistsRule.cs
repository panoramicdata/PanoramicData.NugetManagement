using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a CI workflow exists at .github/workflows/ci.yml.
/// </summary>
public class CiWorkflowExistsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-01";

	/// <inheritdoc />
	public override string RuleName => "CI workflow exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);

		return Task.FromResult(context.FileExists(ciWorkflowPath)
			? Pass($"CI workflow found at {ciWorkflowPath}")
			: Fail(
				$"No CI workflow found at {ciWorkflowPath}",
				new RuleAdvisory
				{
					Summary = $"Create `{ciWorkflowPath}` with build, test, and pack steps",
					Detail = $"Create a GitHub Actions workflow at `{ciWorkflowPath}` that restores, builds in Release configuration, runs tests, and packs NuGet packages.",
					Data = new() { ["expected_path"] = ciWorkflowPath, ["template_content"] = Standards.CiWorkflowContent }
				}));
	}
}
