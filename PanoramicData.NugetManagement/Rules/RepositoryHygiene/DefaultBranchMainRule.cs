using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that repository default branch is main and local work is on main.
/// </summary>
public class DefaultBranchMainRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "REPO-06";

	/// <inheritdoc />
	public override string RuleName => "Default branch and local branch are main";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.RepositoryHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var defaultBranch = context.DefaultBranch.Trim();
		var currentBranch = context.CurrentBranch?.Trim();

		var defaultIsMain = string.Equals(defaultBranch, "main", StringComparison.OrdinalIgnoreCase);
		var currentIsMain = string.IsNullOrWhiteSpace(currentBranch) ||
			string.Equals(currentBranch, "main", StringComparison.OrdinalIgnoreCase);

		if (defaultIsMain && currentIsMain)
		{
			return Task.FromResult(Pass("Default branch is 'main' and local branch is 'main'."));
		}

		var issueParts = new List<string>();
		if (!defaultIsMain)
		{
			issueParts.Add($"default branch is '{defaultBranch}'");
		}

		if (!currentIsMain)
		{
			issueParts.Add($"current local branch is '{currentBranch}'");
		}

		return Task.FromResult(Fail(
			$"Repository branch policy violation: {string.Join(" and ", issueParts)}. Expected 'main'.",
			new RuleAdvisory
			{
				Summary = "Use main as the default branch and local working branch.",
				Detail = "Rename local `master` to `main`, push `main` to origin, switch to `main`, and set GitHub default branch to `main` using `gh repo edit --default-branch main`. If current branch is not `master`, refuse auto-fix and provide AI instructions.",
				Data = new()
				{
					["remediation_type"] = "rename_master_to_main",
					["repo_full_name"] = context.FullName,
					["expected_branch"] = "main",
					["default_branch"] = defaultBranch,
					["current_branch"] = currentBranch ?? string.Empty
				}
			}));
	}
}