using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Web.Remediations.RepositoryHygiene;

/// <summary>
/// Remediates repositories that still use master as default/local branch.
/// </summary>
public sealed class DefaultBranchMainRemediation : IRemediation
{
	/// <inheritdoc />
	public string RuleId => "REPO-06";

	/// <inheritdoc />
	public bool CanRemediate(RuleResult result)
		=> !result.Passed && result.Advisory is not null;

	/// <inheritdoc />
	public void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		if (result.Advisory is null)
		{
			return;
		}

		var repoFullName = TryGetString(result.Advisory.Data, "repo_full_name");

		onOutput?.Invoke("ℹ️ [REPO-06] Starting default branch remediation (master -> main).");

		if (!Run(localPath, "gh", "--version", out _))
		{
			onOutput?.Invoke("❌ [REPO-06] GitHub CLI (gh) is not available on PATH.");
			return;
		}

		if (!Run(localPath, "git", "branch --show-current", out var branchOutput))
		{
			onOutput?.Invoke("❌ [REPO-06] Failed to determine current branch.");
			return;
		}

		var currentBranch = branchOutput.Trim();
		onOutput?.Invoke($"ℹ️ [REPO-06] Current branch: {currentBranch}");

		if (!string.Equals(currentBranch, "master", StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"❌ [REPO-06] Refusing auto-fix: current branch is '{currentBranch}', expected 'master'.");
			foreach (var line in BuildAiFallbackInstructions(repoFullName, currentBranch))
			{
				onOutput?.Invoke(line);
			}

			return;
		}

		var ghRepoViewArgs = string.IsNullOrWhiteSpace(repoFullName)
			? "repo view --json nameWithOwner -q .nameWithOwner"
			: $"repo view {repoFullName} --json nameWithOwner -q .nameWithOwner";

		if (!Run(localPath, "gh", ghRepoViewArgs, out var ghRepoName))
		{
			onOutput?.Invoke("❌ [REPO-06] Pre-flight failed: gh cannot access this repository.");
			return;
		}

		repoFullName = string.IsNullOrWhiteSpace(repoFullName)
			? ghRepoName.Trim()
			: repoFullName;

		onOutput?.Invoke($"ℹ️ [REPO-06] gh repo target: {repoFullName}");
		onOutput?.Invoke($"✅ [REPO-06] Pre-flight gh access confirmed for {repoFullName}.");

		_ = Run(localPath, "git", "status --porcelain", out var porcelainOutput);
		if (!string.IsNullOrWhiteSpace(porcelainOutput))
		{
			onOutput?.Invoke("⚠️ [REPO-06] Working tree is not clean (git status --porcelain is not empty). Continuing as requested.");
		}

		if (!Run(localPath, "git", "branch -m master main", out var renameOutput))
		{
			onOutput?.Invoke($"❌ [REPO-06] Failed to rename branch master -> main. {TrimOutput(renameOutput)}");
			return;
		}

		if (!Run(localPath, "git", "push -u origin main", out var pushOutput))
		{
			onOutput?.Invoke($"❌ [REPO-06] Failed to push main to origin. {TrimOutput(pushOutput)}");
			return;
		}

		if (!Run(localPath, "git", "checkout main", out var checkoutOutput))
		{
			onOutput?.Invoke($"❌ [REPO-06] Failed to switch to main. {TrimOutput(checkoutOutput)}");
			return;
		}

		onOutput?.Invoke($"ℹ️ [REPO-06] Updating GitHub default branch for {repoFullName}.");
		if (!Run(localPath, "gh", $"repo edit {repoFullName} --default-branch main", out var ghEditOutput))
		{
			onOutput?.Invoke($"❌ [REPO-06] Failed to set GitHub default branch to main. {TrimOutput(ghEditOutput)}");
			return;
		}

		if (!Run(localPath, "git", "branch --show-current", out var postBranch) ||
			!string.Equals(postBranch.Trim(), "main", StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"❌ [REPO-06] Post-flight failed: local current branch is '{postBranch.Trim()}', expected 'main'.");
			return;
		}

		if (!Run(localPath, "gh", $"repo view {repoFullName} --json defaultBranchRef -q .defaultBranchRef.name", out var defaultBranchOutput) ||
			!string.Equals(defaultBranchOutput.Trim(), "main", StringComparison.OrdinalIgnoreCase))
		{
			onOutput?.Invoke($"❌ [REPO-06] Post-flight failed: GitHub default branch is '{defaultBranchOutput.Trim()}', expected 'main'.");
			return;
		}

		onOutput?.Invoke("✅ [REPO-06] Post-flight confirmed: local branch is main and GitHub default branch is main.");
		applied.Add("(branch-default-main-remediation)");
	}

	private static string? TryGetString(Dictionary<string, object> data, string key)
	{
		if (data.TryGetValue(key, out var value) && value is string text)
		{
			return text;
		}

		return null;
	}

	private static bool Run(string localPath, string fileName, string arguments, out string output)
	{
		var (exitCode, commandOutput) = LocalRepoService
			.RunCommandAsync(localPath, fileName, arguments)
			.GetAwaiter()
			.GetResult();

		output = commandOutput;
		return exitCode == 0;
	}

	private static string TrimOutput(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
		{
			return "No error output was captured.";
		}

		var flattened = string.Join(" ", output
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
			.Select(part => part.Trim()));

		return flattened.Length <= 300
			? flattened
			: flattened[..300] + "...";
	}

	private static IEnumerable<string> BuildAiFallbackInstructions(string? repoFullName, string currentBranch)
	{
		repoFullName ??= "<owner/repo>";

		return
		[
			"✨ [REPO-06] AI instructions (manual remediation required):",
			"1. Open Copilot Chat in your IDE.",
			$"2. State that auto-fix was refused because branch is '{currentBranch}' instead of 'master'.",
			"3. Ask Copilot to create a safe migration plan from the current branch state to main with no history loss.",
			$"4. Include this repository: {repoFullName}",
			"5. Require commands that verify gh access before and after changing default branch.",
			"6. Require post-flight checks for local branch == main and GitHub default branch == main."
		];
	}
}