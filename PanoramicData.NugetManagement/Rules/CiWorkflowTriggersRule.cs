using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the CI workflow triggers on push and PR to main.
/// </summary>
public class CiWorkflowTriggersRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-02";

	/// <inheritdoc />
	public override string RuleName => "CI triggers on push+PR to main";

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
				"CI workflow not found — cannot check triggers.",
				new RuleAdvisory
				{
					Summary = $"Create `{ciWorkflowPath}` with push and pull_request triggers for the main branch",
					Detail = $"Create `{ciWorkflowPath}` with `on: push: branches: [main]` and `on: pull_request: branches: [main]` triggers.",
					Data = new() { ["expected_path"] = ciWorkflowPath }
				}));
		}

		var hasPush = Contains(content, "push:");
		var hasPr = Contains(content, "pull_request:");
		var hasMain = Contains(content, "main");
		var missingTriggers = new List<string>();
		if (!hasPush)
		{
			missingTriggers.Add("push");
		}

		if (!hasPr)
		{
			missingTriggers.Add("pull_request");
		}

		if (!hasMain)
		{
			missingTriggers.Add("main branch");
		}

		return Task.FromResult(hasPush && hasPr && hasMain
			? Pass("CI workflow triggers on push and pull_request to main.")
			: Fail(
				"CI workflow does not trigger on both push and pull_request to main.",
				new RuleAdvisory
				{
					Summary = "CI workflow should trigger on push and pull_request to main branch",
					Detail = "Add `on: push: branches: [main]` and `on: pull_request: branches: [main]` to the CI workflow.",
					Data = new()
					{
						["workflow_file"] = ciWorkflowPath,
						["missing_triggers"] = missingTriggers.ToArray()
					}
				}));
	}
}
