using System.Text;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Formats Codacy issues into a grouped summary line, a full markdown detail report (used both in
/// the AI remediation prompt and as a downloadable report), and machine-readable advisory data.
/// </summary>
public static class CodacyReportFormatter
{
	/// <summary>
	/// Builds a concise, grouped one-line summary for the rule message and UI display.
	/// </summary>
	public static string BuildSummary(IReadOnlyList<CodacyIssue> issues)
	{
		if (issues.Count == 0)
		{
			return "Codacy reports no open issues.";
		}

		var byCategory = issues
			.GroupBy(i => string.IsNullOrWhiteSpace(i.Category) ? "Uncategorised" : i.Category!)
			.OrderByDescending(g => g.Count())
			.Select(g => $"{g.Count()} {g.Key}");

		var distinctPatterns = issues.Select(i => i.PatternId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

		return $"Codacy reported {issues.Count} issue(s) across {distinctPatterns} pattern(s): {string.Join(", ", byCategory)}.";
	}

	/// <summary>
	/// Builds the full markdown report listing every issue grouped by pattern. Suitable for an AI
	/// remediation prompt and for download as a standalone <c>.md</c> file (so the detail need not
	/// be re-fetched from Codacy).
	/// </summary>
	public static string BuildDetailMarkdown(string repositoryFullName, IReadOnlyList<CodacyIssue> issues)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"# Codacy issues — {repositoryFullName}");
		sb.AppendLine();
		sb.AppendLine($"{issues.Count} open issue(s) reported by Codacy.");
		sb.AppendLine();

		if (issues.Count == 0)
		{
			return sb.ToString();
		}

		var groups = issues
			.GroupBy(i => i.PatternId ?? "(unknown pattern)")
			.OrderByDescending(g => g.Count())
			.ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

		foreach (var group in groups)
		{
			var first = group.First();
			var category = string.IsNullOrWhiteSpace(first.Category) ? "Uncategorised" : first.Category;
			var severity = string.IsNullOrWhiteSpace(first.Severity) ? "" : $", {first.Severity}";
			sb.AppendLine($"## {group.Key} — {category}{severity} ({group.Count()})");
			sb.AppendLine();

			foreach (var issue in group
				.OrderBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(i => i.Line))
			{
				var location = issue.Line > 0 ? $"{issue.FilePath}:{issue.Line}" : issue.FilePath;
				sb.AppendLine($"- `{location}` — {issue.Message}");
			}

			sb.AppendLine();
		}

		return sb.ToString();
	}

	/// <summary>
	/// Builds the machine-readable advisory data (counts and breakdowns).
	/// </summary>
	public static Dictionary<string, object> BuildAdvisoryData(IReadOnlyList<CodacyIssue> issues)
	{
		return new Dictionary<string, object>
		{
			["source"] = "codacy",
			["total_issues"] = issues.Count,
			["by_category"] = issues
				.GroupBy(i => string.IsNullOrWhiteSpace(i.Category) ? "Uncategorised" : i.Category!)
				.ToDictionary(g => g.Key, g => g.Count()),
			["by_pattern"] = issues
				.GroupBy(i => i.PatternId ?? "(unknown pattern)")
				.OrderByDescending(g => g.Count())
				.ToDictionary(g => g.Key, g => g.Count())
		};
	}
}
