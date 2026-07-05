using System.Text;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Builds a single consolidated AI remediation prompt that spans every repository affected by an
/// issue class (or category). Used for issues that have no automated remediation, so the full
/// per-repository detail can be pasted into one AI session without re-fetching.
/// </summary>
public static class CombinedRemediationPromptBuilder
{
	/// <summary>
	/// Builds a combined prompt for a single issue class across all affected repositories.
	/// </summary>
	public static string ForRule(IssueClassGroup issueClass)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"# Fix: {issueClass.RuleName} ({issueClass.RuleId})");
		sb.AppendLine();
		sb.AppendLine($"This issue affects **{issueClass.AffectedRepositoryCount} repositor{(issueClass.AffectedRepositoryCount == 1 ? "y" : "ies")}**. Apply the fix in each repository listed below.");
		sb.AppendLine();

		var summary = issueClass.Instances
			.Select(i => i.Result.Advisory?.Summary)
			.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
		if (!string.IsNullOrWhiteSpace(summary))
		{
			sb.AppendLine($"**Goal:** {summary}");
			sb.AppendLine();
		}

		foreach (var instance in issueClass.Instances)
		{
			AppendInstance(sb, instance);
		}

		return sb.ToString();
	}

	/// <summary>
	/// Builds a combined prompt for every issue class in a category across all affected repositories.
	/// </summary>
	/// <param name="category">The category grouping.</param>
	/// <param name="onlyNonRemediable">
	/// When true, issue classes that can be auto-remediated are excluded (they are handled by the
	/// bulk apply action instead), leaving only the manual/AI issues in the prompt.
	/// </param>
	public static string ForCategory(IssueCategoryGroup category, bool onlyNonRemediable = false)
	{
		var included = category.IssueClasses
			.Where(i => !onlyNonRemediable || !i.HasAutomatedRemediation)
			.ToList();

		var sb = new StringBuilder();
		sb.AppendLine($"# Fix {category.Category} issues across the organization");
		sb.AppendLine();
		sb.AppendLine($"{included.Count} issue class(es) require attention. Address each section in the affected repositories.");
		sb.AppendLine();

		foreach (var issueClass in included)
		{
			sb.AppendLine($"---");
			sb.AppendLine();
			sb.AppendLine($"## {issueClass.RuleName} ({issueClass.RuleId}) — {issueClass.AffectedRepositoryCount} repo(s)");
			sb.AppendLine();
			foreach (var instance in issueClass.Instances)
			{
				AppendInstance(sb, instance, headingLevel: 3);
			}
		}

		return sb.ToString();
	}

	private static void AppendInstance(StringBuilder sb, IssueInstance instance, int headingLevel = 2)
	{
		sb.AppendLine($"{new string('#', headingLevel)} {instance.RepositoryFullName}");
		sb.AppendLine();
		sb.AppendLine(instance.Result.Message);
		sb.AppendLine();

		if (instance.Result.Advisory is { } advisory && !string.IsNullOrWhiteSpace(advisory.Detail))
		{
			sb.AppendLine(advisory.Detail);
			sb.AppendLine();
		}
	}
}
