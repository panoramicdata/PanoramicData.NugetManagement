using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Web.Remediations.RepositoryHygiene;

/// <summary>
/// Remediates repositories that still use master as default/local branch.
/// </summary>
/// <remarks>
/// Renamed on GitHub through the branch rename API, which is atomic, sets the default branch and
/// retargets open pull requests; nothing is pushed, so branch protection on main is no obstacle. The
/// clone is brought into line afterwards. Success is recorded only when GitHub accepted the rename.
/// </remarks>
public sealed partial class DefaultBranchMainRemediation : IRemediation
{
	private readonly Func<string, string, IReadOnlyList<string>, (int ExitCode, string Output)> _run;

	/// <summary>
	/// Initializes a new instance of the <see cref="DefaultBranchMainRemediation"/> class.
	/// </summary>
	public DefaultBranchMainRemediation()
		: this((workingDirectory, fileName, arguments) => LocalRepoService
			.RunCommandAsync(workingDirectory, fileName, arguments, CancellationToken.None)
			.GetAwaiter()
			.GetResult())
	{
	}

	/// <summary>
	/// Initializes a new instance with an explicit command runner (for testing).
	/// </summary>
	/// <param name="run">Runs a program (<c>git</c> or <c>gh</c>) in a working directory with the given arguments.</param>
	internal DefaultBranchMainRemediation(Func<string, string, IReadOnlyList<string>, (int ExitCode, string Output)> run)
		=> _run = run;

	/// <inheritdoc />
	public string RuleId => "REPO-06";

	/// <inheritdoc />
	public bool CanRemediate(RuleResult result)
		=> !result.Passed && TryGetRepository(result) is not null;

	/// <inheritdoc />
	public void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		var repository = TryGetRepository(result);
		if (repository is null)
		{
			onOutput?.Invoke("❌ [REPO-06] The advisory names no valid owner/name repository, so nothing was renamed.");
			return;
		}

		var (_, branchOutput) = _run(localPath, "git", ["branch", "--show-current"]);
		var currentBranch = branchOutput.Trim();
		if (!string.Equals(currentBranch, "master", StringComparison.Ordinal))
		{
			onOutput?.Invoke($"❌ [REPO-06] Refusing auto-fix: current branch is '{currentBranch}', expected 'master'.");
			foreach (var line in BuildAiFallbackInstructions(repository, currentBranch))
			{
				onOutput?.Invoke(line);
			}

			return;
		}

		var (exitCode, output) = _run(localPath, "gh", ["api", "-X", "POST", $"repos/{repository}/branches/master/rename", "-f", "new_name=main"]);
		if (exitCode != 0)
		{
			onOutput?.Invoke($"❌ [REPO-06] GitHub refused to rename master to main for {repository} (admin rights are needed): {TrimOutput(output)}");
			return;
		}

		applied.Add($"(branch-default-main-remediation:{repository})");
		onOutput?.Invoke($"✅ [REPO-06] Renamed master to main on GitHub for {repository}.");
		AlignClone(localPath, onOutput);
	}

	private void AlignClone(string localPath, Action<string>? onOutput)
	{
		string[][] steps =
		[
			["fetch", "--prune", "origin"],
			["branch", "-m", "master", "main"],
			["branch", "-u", "origin/main", "main"],
			["remote", "set-head", "origin", "-a"]
		];

		foreach (var step in steps)
		{
			var (exitCode, output) = _run(localPath, "git", step);
			if (exitCode != 0)
			{
				onOutput?.Invoke($"⚠️ [REPO-06] GitHub is renamed, but 'git {string.Join(' ', step)}' failed in the clone: {TrimOutput(output)}");
				return;
			}
		}

		onOutput?.Invoke("✅ [REPO-06] The clone now tracks origin/main.");
	}

	/// <summary>
	/// The repository the advisory names, or null unless it is a plain owner/name. The value ends up in a
	/// command line, so anything else is refused rather than escaped.
	/// </summary>
	private static string? TryGetRepository(RuleResult result)
		=> result.Advisory?.Data.TryGetValue("repo_full_name", out var value) == true
			&& value is string repository
			&& OwnerNamePattern().IsMatch(repository)
				? repository
				: null;

	[GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
	private static partial Regex OwnerNamePattern();

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

	private static IEnumerable<string> BuildAiFallbackInstructions(string repoFullName, string currentBranch)
		=>
		[
			"✨ [REPO-06] AI instructions (manual remediation required):",
			"1. Open Copilot Chat in your IDE.",
			$"2. State that auto-fix was refused because branch is '{currentBranch}' instead of 'master'.",
			"3. Ask Copilot to create a safe migration plan from the current branch state to main with no history loss.",
			$"4. Include this repository: {repoFullName}",
			"5. Prefer the GitHub branch rename API over pushing a new main, since main is protected.",
			"6. Require post-flight checks for local branch == main and GitHub default branch == main."
		];
}
