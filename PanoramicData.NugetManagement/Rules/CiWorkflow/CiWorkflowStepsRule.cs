using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the CI workflow restores, builds Release, and packs artifacts.
/// </summary>
public class CiWorkflowStepsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-03";

	/// <inheritdoc />
	public override string RuleName => "CI restores, builds Release, and packs";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);
		var content = context.GetFileContent(ciWorkflowPath);
		if (content is null)
		{
			return Task.FromResult(Fail(
				"CI workflow not found — cannot check steps.",
				new RuleAdvisory
				{
					Summary = $"Create `{ciWorkflowPath}` with restore, build (Release), and pack steps",
					Detail = $"Create `{ciWorkflowPath}` with `dotnet restore`, `dotnet build --configuration Release`, and `dotnet pack` steps. A `dotnet test` step is optional.",
					Data = new() { ["expected_path"] = ciWorkflowPath }
				}));
		}

		string[] requiredSteps = ["dotnet restore", "dotnet build", "--configuration Release", "dotnet pack"];
		var missingSteps = requiredSteps.Where(step => !Contains(content, step)).ToArray();

		return Task.FromResult(missingSteps.Length == 0
			? Pass("CI workflow contains restore, build (Release), and pack steps.")
			: Fail(
				"CI workflow is missing one or more required steps (restore, build --configuration Release, pack).",
				new RuleAdvisory
				{
					Summary = "CI workflow must include restore, build (Release), and pack steps",
					Detail = "Ensure the CI workflow has `dotnet restore`, `dotnet build --configuration Release`, and `dotnet pack` steps.",
					Data = new()
					{
						["workflow_file"] = ciWorkflowPath,
						["missing_steps"] = missingSteps
					}
				}));
	}
}
