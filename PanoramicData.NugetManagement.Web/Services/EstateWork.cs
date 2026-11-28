using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Which repositories an estate-wide press of a step has known work in, and what that work is.
/// </summary>
/// <remarks>
/// A repository is left out only when the app positively knows there is nothing to do; an unknown
/// state counts as work, because finding out is part of what the step does.
/// </remarks>
public static class EstateWork
{
	private const int NamesShown = 8;

	/// <summary>
	/// How many auto-fixes Fix could apply to this repository right now, leaving out the synthesised
	/// Dependabot findings, which triage handles.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="isAutoRemediable">Whether a failing rule has an auto-remediation.</param>
	public static int RemediationCount(RepositoryDashboardRow row, Func<RuleResult, bool> isAutoRemediable)
	{
		var failures = RemediableFailures(row, isAutoRemediable);
		return CanApplyRemediations(row, failures) ? failures.Count : 0;
	}

	/// <summary>
	/// Whether the repository has auto-fixes that its branch or sync state stops Fix applying.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="isAutoRemediable">Whether a failing rule has an auto-remediation.</param>
	public static bool HasBlockedRemediations(RepositoryDashboardRow row, Func<RuleResult, bool> isAutoRemediable)
	{
		var failures = RemediableFailures(row, isAutoRemediable);
		return failures.Count > 0 && row.IsClonedLocally && !CanApplyRemediations(row, failures);
	}

	/// <summary>
	/// How many open Dependabot pull requests triage would judge, or zero when the inbox is unread or
	/// there is no assessment to judge them against.
	/// </summary>
	/// <param name="row">The repository.</param>
	public static int DependabotPullRequestCount(RepositoryDashboardRow row)
		=> row.Assessment is null || !row.OpenIssuesKnown
			? 0
			: row.OpenIssues.Count(issue =>
				issue.IsPullRequest
				&& string.Equals(issue.AuthorLogin, "dependabot[bot]", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Whether pressing <paramref name="step"/> would do anything in this repository.
	/// </summary>
	/// <param name="step">The step.</param>
	/// <param name="row">The repository.</param>
	/// <param name="isAutoRemediable">Whether a failing rule has an auto-remediation.</param>
	/// <param name="nowUtc">The moment to age a remembered sync answer against.</param>
	public static bool HasWork(
		WorkflowStep step,
		RepositoryDashboardRow row,
		Func<RuleResult, bool> isAutoRemediable,
		DateTimeOffset nowUtc) => step switch
		{
			WorkflowStep.Fix => RemediationCount(row, isAutoRemediable) > 0 || DependabotPullRequestCount(row) > 0,
			WorkflowStep.GitSync => !row.IsClonedLocally || !RepositoryGitState.IsRecentlyConfirmedInSync(row, nowUtc),
			WorkflowStep.CommitAndPush => !RepositoryGitState.IsPorcelain(row),

			// Build and Test always have work: a remembered result is not cleared by edits made outside
			// the app, so it cannot prove the current tree builds.
			_ => true
		};

	/// <summary>
	/// What an estate-wide press of <paramref name="step"/> will do, naming the repositories, and what
	/// it leaves out and why.
	/// </summary>
	/// <param name="step">The step.</param>
	/// <param name="stepName">The step's label, as the button shows it.</param>
	/// <param name="eligible">The ticked repositories the step may act on, before work is considered.</param>
	/// <param name="selectedCount">How many repositories are ticked.</param>
	/// <param name="isAutoRemediable">Whether a failing rule has an auto-remediation.</param>
	/// <param name="nowUtc">The moment to age a remembered sync answer against.</param>
	public static string Describe(
		WorkflowStep step,
		string stepName,
		IReadOnlyList<RepositoryDashboardRow> eligible,
		int selectedCount,
		Func<RuleResult, bool> isAutoRemediable,
		DateTimeOffset nowUtc)
	{
		if (selectedCount == 0)
		{
			return $"{stepName}: tick repositories to act on them.";
		}

		var withWork = eligible.Where(row => HasWork(step, row, isAutoRemediable, nowUtc)).ToList();
		var sentences = new List<string>();

		if (withWork.Count == 0)
		{
			sentences.Add($"{stepName} has nothing to do in the {selectedCount} ticked {Repositories(selectedCount)}.");
		}
		else
		{
			sentences.Add(step == WorkflowStep.Fix
				? DescribeFix(withWork, isAutoRemediable)
				: $"{stepName} will {Action(step, stepName)} {withWork.Count} {Repositories(withWork.Count)}"
					+ $"{Qualifier(step)}: {Names(withWork)}.");
		}

		var withoutWork = eligible.Except(withWork).ToList();

		if (step == WorkflowStep.Fix)
		{
			withoutWork = DescribeFixLeftOut(eligible, withoutWork, isAutoRemediable, sentences);
		}

		if (withoutWork.Count > 0)
		{
			sentences.Add($"{withoutWork.Count} {Repositories(withoutWork.Count)} {AlreadyDone(step, withoutWork.Count)}.");
		}

		var skipped = selectedCount - eligible.Count;
		if (skipped > 0)
		{
			sentences.Add($"{skipped} ticked {Repositories(skipped)} skipped: excluded, not governed, or not cloned locally.");
		}

		return string.Join(" ", sentences);
	}

	// Says which repositories Fix is blocked in or cannot judge, and returns those that truly have nothing to fix.
	private static List<RepositoryDashboardRow> DescribeFixLeftOut(
		IReadOnlyList<RepositoryDashboardRow> eligible,
		List<RepositoryDashboardRow> withoutWork,
		Func<RuleResult, bool> isAutoRemediable,
		List<string> sentences)
	{
		var blocked = eligible.Where(row => HasBlockedRemediations(row, isAutoRemediable)).ToList();
		if (blocked.Count > 0)
		{
			sentences.Add($"Auto-fixes in {Names(blocked)} wait until {(blocked.Count == 1 ? "it is" : "they are")} "
				+ "on main and not behind origin.");
		}

		var unknown = withoutWork.Except(blocked).Where(row => row.Assessment is null || !row.OpenIssuesKnown).ToList();
		if (unknown.Count > 0)
		{
			sentences.Add($"{unknown.Count} {Repositories(unknown.Count)} {Is(unknown.Count)} "
				+ "not fully assessed yet, so Fix cannot tell what to do there: Re-assess first.");
		}

		return [.. withoutWork.Except(blocked).Except(unknown)];
	}

	private static string DescribeFix(
		IReadOnlyList<RepositoryDashboardRow> withWork,
		Func<RuleResult, bool> isAutoRemediable)
	{
		var fixing = withWork.Where(row => RemediationCount(row, isAutoRemediable) > 0).ToList();
		var triaging = withWork.Where(row => DependabotPullRequestCount(row) > 0).ToList();
		var clauses = new List<string>();

		if (fixing.Count > 0)
		{
			var fixes = fixing.Sum(row => RemediationCount(row, isAutoRemediable));
			clauses.Add($"apply {fixes} auto-{(fixes == 1 ? "fix" : "fixes")} in {Names(fixing)}");
		}

		if (triaging.Count > 0)
		{
			var pullRequests = triaging.Sum(DependabotPullRequestCount);
			clauses.Add($"triage {pullRequests} Dependabot pull {(pullRequests == 1 ? "request" : "requests")} in {Names(triaging)}");
		}

		return $"Fix will {string.Join(", and ", clauses)}.";
	}

	private static List<RuleResult> RemediableFailures(RepositoryDashboardRow row, Func<RuleResult, bool> isAutoRemediable)
		=> row.Assessment?.RuleResults
			.Where(r => !r.Passed && !DependabotIssueSynthesizer.IsSynthetic(r.RuleId) && isAutoRemediable(r))
			.ToList() ?? [];

	// The same gate the single-repository Fix applies: REPO-06 moves the clone onto main itself, and
	// every other remediation needs a clone on main that is not behind origin.
	private static bool CanApplyRemediations(RepositoryDashboardRow row, List<RuleResult> failures)
		=> row.IsClonedLocally
			&& (failures.Any(r => r.RuleId == "REPO-06")
				|| (row.CurrentBranch == "main" && row.IsSyncedWithOrigin != false));

	private static string Action(WorkflowStep step, string stepName) => step switch
	{
		WorkflowStep.GitSync => "fetch and fast-forward",
		WorkflowStep.Build => "build",
		WorkflowStep.Test => "run the tests of",
		WorkflowStep.CommitAndPush => "open a pull request into main for",
		_ => $"run {stepName.ToLowerInvariant()} on"
	};

	private static string Qualifier(WorkflowStep step) => step switch
	{
		WorkflowStep.GitSync => " not recently confirmed in sync with origin",
		WorkflowStep.CommitAndPush => " with uncommitted or unpushed changes",
		_ => string.Empty
	};

	private static string AlreadyDone(WorkflowStep step, int count) => step switch
	{
		WorkflowStep.Fix => $"{Has(count)} nothing to fix",
		WorkflowStep.GitSync => $"{Is(count)} already in sync with origin",
		WorkflowStep.CommitAndPush => $"{Has(count)} nothing to commit or push",
		_ => $"{Has(count)} nothing to do"
	};

	private static string Names(IReadOnlyList<RepositoryDashboardRow> rows)
	{
		var listed = string.Join(", ", rows.Take(NamesShown).Select(row => row.RepositoryName));
		var more = rows.Count - NamesShown;
		return more > 0 ? $"{listed} and {more} more" : listed;
	}

	private static string Is(int count) => count == 1 ? "is" : "are";

	private static string Has(int count) => count == 1 ? "has" : "have";

	private static string Repositories(int count) => count == 1 ? "repository" : "repositories";
}
