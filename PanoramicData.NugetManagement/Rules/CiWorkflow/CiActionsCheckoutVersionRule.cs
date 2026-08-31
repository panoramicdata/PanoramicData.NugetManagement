using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the CI workflow uses the latest actions/checkout version.
/// </summary>
public class CiActionsCheckoutVersionRule : RuleBase, IGovernsDependency
{
	/// <inheritdoc />
	public override string RuleId => "CI-05";

	/// <inheritdoc />
	public bool Governs(DependencyRef dependency)
		=> dependency == new DependencyRef(DependencyEcosystem.GitHubActions, "actions/checkout");

	/// <inheritdoc />
	public override string RuleName => "CI uses latest actions/checkout";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);
		var content = context.GetFileContent(ciWorkflowPath);
		if (content is null)
		{
			return Task.FromResult(Fail(
				"CI workflow not found.",
				new RuleAdvisory
				{
					Summary = $"Create `{ciWorkflowPath}` and use `actions/checkout@{Standards.LatestActionsCheckoutVersion}`",
					Detail = $"Create `{ciWorkflowPath}` using `actions/checkout@{Standards.LatestActionsCheckoutVersion}`.",
					Data = new()
					{
						["expected_path"] = ciWorkflowPath,
						["latest_version"] = Standards.LatestActionsCheckoutVersion
					}
				}));
		}

		var floor = Services.ActionVersionCatalog.Default.GetFloorSpec("actions/checkout", Standards.LatestActionsCheckoutVersion);
		var meetsFloor = GitHubActionVersion.UsesAtLeast(content, "actions/checkout", floor, out var used);
		if (used is not null)
		{
			Services.ActionVersionCatalog.Default.Observe("actions/checkout", used.Value, Standards.LatestActionsCheckoutVersion, context.FullName);
		}

		if (meetsFloor)
		{
			return Task.FromResult(Pass($"CI uses actions/checkout@v{used} (at or above {floor})."));
		}

		var data = new Dictionary<string, object>
		{
			["workflow_file"] = ciWorkflowPath,
			["minimum_version"] = floor,
			["found_version"] = used is null ? "none" : $"v{used}"
		};

		// Bumping a version already written in the file is a rewrite of text this rule has located and
		// understood, so it needs no judgement. Adding a checkout step that is absent altogether does,
		// and is left to the AI: where it belongs in the job is not something this rule knows.
		if (used is not null)
		{
			data["remediation_type"] = "replace_regex_in_file";
			data["file"] = ciWorkflowPath;
			data["patterns"] = new[] { @"(actions/checkout@)v\d+(\.\d+)*" };
			data["replacements"] = new[] { $"${{1}}{floor}" };
		}

		return Task.FromResult(Fail(
			used is null
				? "CI does not use actions/checkout."
				: $"CI uses actions/checkout@v{used}; expected {floor} or later.",
			new RuleAdvisory
			{
				Summary = $"Update actions/checkout to {floor} or later",
				Detail = $"Update the checkout step to `uses: actions/checkout@{floor}` (or a later major version).",
				Data = data
			}));
	}
}
