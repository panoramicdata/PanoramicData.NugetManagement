using System.Globalization;
using Microsoft.Extensions.Options;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class DashboardService
{
	/// <summary>
	/// Generates an AI remediation prompt from failed rules.
	/// </summary>
	public static string GenerateRemediationPrompt(RepositoryDashboardRow row, bool includeInfo)
	{
		if (row.Assessment is null)
		{
			return string.Empty;
		}

		var failures = row.Assessment.RuleResults
			.Where(r => !r.Passed && (includeInfo || r.Severity != AssessmentSeverity.Info))
			.ToList();
		return GeneratePromptFromFailures(row, failures);
	}

	/// <summary>
	/// Generates an AI remediation prompt for a specific category's failed rules.
	/// </summary>
	public static string GenerateCategoryRemediationPrompt(RepositoryDashboardRow row, AssessmentCategory category, bool includeInfo)
	{
		if (row.Assessment is null)
		{
			return string.Empty;
		}

		var failures = row.Assessment!.RuleResults
			.Where(r => !r.Passed && r.Category == category && (includeInfo || r.Severity != AssessmentSeverity.Info))
			.ToList();
		return GeneratePromptFromFailures(row, failures);
	}

	/// <summary>
	/// Generates an AI remediation prompt for a single failed rule.
	/// </summary>
	public static string GenerateRuleRemediationPrompt(RepositoryDashboardRow row, RuleResult result)
	{
		if (row.Assessment is null || result.Passed)
		{
			return string.Empty;
		}

		return GeneratePromptFromFailures(row, [result]);
	}

	/// <summary>
	/// Gets the full Codacy issue report (markdown) for a repository, suitable for download as a
	/// standalone <c>.md</c> file or for pasting into an AI session. Returns null when the Codacy
	/// issues rule (CQ-05) did not run or found nothing to report.
	/// </summary>
	public static string? GetCodacyReportMarkdown(RepositoryDashboardRow row)
		=> row.Assessment?.RuleResults
			.FirstOrDefault(r => r.RuleId == "CQ-05" && r.Advisory is not null)
			?.Advisory?.Detail;

	/// <summary>
	/// Generates an AI remediation prompt from an explicit set of rule failures.
	/// </summary>
	public static string GenerateRemediationPromptForFailures(RepositoryDashboardRow row, IEnumerable<RuleResult> failures, bool includeInfo)
	{
		var filtered = failures
			.Where(r => !r.Passed && (includeInfo || r.Severity != AssessmentSeverity.Info))
			.ToList();

		return GeneratePromptFromFailures(row, filtered);
	}

	/// <summary>
	/// Generates an AI remediation prompt for a build or test workflow failure using recent console output.
	/// </summary>
	public static string GenerateWorkflowFailurePrompt(RepositoryDashboardRow row, string workflowArea, IEnumerable<string> consoleLines)
	{
		var excerpt = consoleLines
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.TakeLast(120)
			.ToList();

		var title = workflowArea.Equals("test", StringComparison.OrdinalIgnoreCase)
			? "Test Failure"
			: "Build Failure";

		var targetOutcome = workflowArea.Equals("test", StringComparison.OrdinalIgnoreCase)
			? "make the tests pass"
			: "make the project build successfully";

		var lines = new List<string>
		{
			$"# {title} Fix Instructions for {row.RepositoryFullName}",
			$"Repository: {row.RepositoryFullName}",
			$"Local path: {row.LocalPath}",
			$"Current status: {row.Status}",
			$"Requested outcome: {targetOutcome}.",
			"",
			"Please inspect the recent console output below, identify the root cause, apply the minimum code changes needed, and then rerun the failing step.",
			""
		};

		if (excerpt.Count > 0)
		{
			lines.Add("## Recent Console Output");
			lines.Add("```text");
			lines.AddRange(excerpt);
			lines.Add("```");
		}
		else if (!string.IsNullOrWhiteSpace(row.StatusMessage))
		{
			lines.Add("## Failure Summary");
			lines.Add(row.StatusMessage);
		}

		return string.Join('\n', lines);
	}

	/// <summary>
	/// Generates a concise AI remediation prompt for build/test failures using high-signal log lines.
	/// </summary>
	public static string GenerateConciseWorkflowFailurePrompt(RepositoryDashboardRow row, string workflowArea, IEnumerable<string> consoleLines)
	{
		var isTest = workflowArea.Equals("test", StringComparison.OrdinalIgnoreCase);
		var title = isTest ? "Test Failure" : "Build Failure";
		var action = isTest ? "dotnet test" : "dotnet build";

		var keyLines = SelectKeyLines(consoleLines);

		var lines = new List<string>
		{
			$"# {title} Fix Request",
			$"Repository: {row.RepositoryFullName}",
			$"Local path: {row.LocalPath}",
			"",
			$"Please fix the root cause of the {workflowArea} failure with the minimum safe code changes, then rerun `{action}`.",
			"",
			"## Key Output",
			"```text"
		};

		if (keyLines.Count > 0)
		{
			lines.AddRange(keyLines);
		}
		else if (!string.IsNullOrWhiteSpace(row.StatusMessage))
		{
			lines.Add(row.StatusMessage);
		}

		lines.Add("```");
		return string.Join('\n', lines);
	}

	private static List<string> SelectKeyLines(IEnumerable<string> consoleLines)
	{
		var keyLines = consoleLines
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.Where(line =>
				line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
				line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
				line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
				line.Contains("assert", StringComparison.OrdinalIgnoreCase))
			.TakeLast(24)
			.ToList();

		return keyLines.Count > 0
			? keyLines
			: [.. consoleLines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(24)];
	}

	/// <summary>
	/// Orders rule failures worst-first: Critical and Error, then Warning, then everything else.
	/// Shared by the prompt builder and the on-screen grouped failure list so the two cannot drift.
	/// </summary>
	public static int SeverityRank(AssessmentSeverity severity) => severity switch
	{
		AssessmentSeverity.Critical or AssessmentSeverity.Error => 0,
		AssessmentSeverity.Warning => 1,
		_ => 2
	};

	/// <summary>
	/// Orders a category by the severity of its worst failure, so the most serious category leads.
	/// </summary>
	public static int CategoryRank(IEnumerable<RuleResult> failures)
		=> failures.Select(f => SeverityRank(f.Severity)).DefaultIfEmpty(2).Min();

	private static string GeneratePromptFromFailures(RepositoryDashboardRow row, List<RuleResult> failures)
	{
		if (failures.Count == 0)
		{
			return string.Empty;
		}

		var groups = failures
			.GroupBy(f => f.Category)
			.Select(g => new { Category = g.Key, Failures = g.OrderBy(f => SeverityRank(f.Severity)).ThenBy(f => f.RuleId).ToList() })
			.OrderBy(g => CategoryRank(g.Failures))
			.ThenBy(g => g.Category.ToString(), StringComparer.Ordinal)
			.ToList();

		// A single category needs no section headings — keep the flat form the per-category and
		// per-rule prompts have always produced. Multiple categories get sections so the AI sees
		// the whole picture, grouped and ordered the same way the UI presents it.
		var grouped = groups.Count > 1;

		var lines = new List<string>
		{
			$"# Remediation Instructions for {row.RepositoryFullName}",
			$"Repository: {row.RepositoryFullName}",
			$"Local path: {row.LocalPath}",
			""
		};

		if (grouped)
		{
			lines.Add("Please fix the following governance issues, grouped by category and ordered by severity — start with the most serious.");
		}
		else
		{
			lines.Add("Please fix the following governance issues:");
		}

		lines.Add("");

		var ruleHeadingPrefix = grouped ? "###" : "##";

		foreach (var group in groups)
		{
			if (grouped)
			{
				var count = group.Failures.Count;
				lines.Add($"## {group.Category} ({count} {(count == 1 ? "issue" : "issues")})");
				lines.Add("");
			}

			foreach (var failure in group.Failures)
			{
				AppendFailureSection(lines, failure, ruleHeadingPrefix);
			}
		}

		return string.Join('\n', lines);
	}

	private static void AppendFailureSection(List<string> lines, RuleResult failure, string ruleHeadingPrefix)
	{
		lines.Add($"{ruleHeadingPrefix} [{failure.RuleId}] {failure.RuleName}");
		lines.Add($"- **Issue**: {failure.Message}");
		if (failure.Advisory is not null)
		{
			lines.Add($"- **Fix**: {failure.Advisory.Detail}");
			foreach (var (key, value) in failure.Advisory.Data)
			{
				// Nested facts — CQ-06's per-file grades, and every rule that reports a
				// list of dictionaries — are expanded rather than joined, so the model
				// gets the figures instead of a row of type names.
				lines.AddRange(AiFixPrompt
					.RenderFact(key, value, "  ", markdown: true)
					.Split('\n', StringSplitOptions.RemoveEmptyEntries)
					.Select(line => line.TrimEnd('\r')));
			}
		}

		lines.Add("");
	}

	/// <summary>
	/// Gets the remediation registry for checking fix availability.
	/// </summary>
	public RemediationRegistry RemediationRegistry { get; }

	/// <summary>
	/// Builds the issue-centric view (Category → Rule → Repository) from the assessed rows,
	/// marking each occurrence's auto-remediability via the remediation registry.
	/// </summary>
	public IssueCentricView BuildIssueCentricView(IEnumerable<RepositoryDashboardRow> rows)
	{
		// Pass the package id through: a repository hosting several packages appears once per package
		// under a rule, and this is what tells those occurrences apart in the issue tree.
		//
		// Augmented so that open Dependabot pull requests appear here too, one node per dependency —
		// otherwise a backlog spread across the estate can only be cleared a repository at a time.
		var entries = rows
			.Where(r => r.RepositoryFullName is not null && r.Assessment is not null)
			.Select(r => new AssessedPackage(
				r.RepositoryFullName,
				DependabotIssueSynthesizer.Augment(r),
				r.RepositoryFullName));
		return IssueCentricViewBuilder.Build(entries, IsAutoRemediable);
	}

	/// <summary>
	/// Generates a single consolidated AI prompt for one issue class across every affected repository.
	/// </summary>
	public string GenerateCombinedRulePrompt(IEnumerable<RepositoryDashboardRow> rows, string ruleId)
	{
		var issueClass = BuildIssueCentricView(rows).AllIssueClasses
			.FirstOrDefault(i => string.Equals(i.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
		return issueClass is null ? string.Empty : CombinedRemediationPromptBuilder.ForRule(issueClass);
	}

	/// <summary>
	/// Generates a single consolidated AI prompt for a category across every affected repository.
	/// </summary>
	public string GenerateCombinedCategoryPrompt(IEnumerable<RepositoryDashboardRow> rows, AssessmentCategory category, bool onlyNonRemediable)
	{
		var group = BuildIssueCentricView(rows).Categories.FirstOrDefault(c => c.Category == category);
		return group is null ? string.Empty : CombinedRemediationPromptBuilder.ForCategory(group, onlyNonRemediable);
	}
}
