using System.Text;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Formats a band of Codacy security findings into the rule message, the markdown advisory an AI
/// session is given, and the machine-readable advisory data.
/// </summary>
/// <remarks>
/// A band is named rather than typed as a single <see cref="CodacySecurityPriority"/>, because
/// SEC-03 owns two priorities at once and calling that band "Medium" would misreport every Low
/// finding in it.
/// </remarks>
public static class CodacySecurityFormatter
{
	/// <summary>The label for a finding Codacy has left uncategorised.</summary>
	private const string Uncategorised = "Uncategorised";

	/// <summary>
	/// Builds the one-line rule message, grouped by security category so the reader sees what kind
	/// of problem this is before deciding whether to open it.
	/// </summary>
	/// <param name="bandName">How this rule's severity band is named in prose, e.g. "Critical".</param>
	/// <param name="findings">The findings in the band.</param>
	public static string BuildSummary(string bandName, IReadOnlyList<CodacySecurityFinding> findings)
	{
		if (findings.Count == 0)
		{
			return $"Codacy reports no open {bandName} security findings.";
		}

		var byCategory = findings
			.GroupBy(CategoryOf, StringComparer.OrdinalIgnoreCase)
			.OrderByDescending(group => group.Count())
			.ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
			.Select(group => $"{group.Count()} {group.Key}");

		var overdue = CountOverdue(findings);
		var overdueNote = overdue > 0 ? $" {overdue} past the SLA due date." : string.Empty;

		return $"Codacy reports {findings.Count} open {bandName} security finding(s): "
			+ $"{string.Join(", ", byCategory)}.{overdueNote}";
	}

	/// <summary>
	/// Builds the markdown advisory listing every finding in the band, grouped by category, with a
	/// link to each one in Codacy.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as "organization/repository".</param>
	/// <param name="bandName">How this rule's severity band is named in prose.</param>
	/// <param name="findings">The findings in the band.</param>
	public static string BuildDetailMarkdown(
		string repositoryFullName,
		string bandName,
		IReadOnlyList<CodacySecurityFinding> findings)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"# Codacy {bandName} security findings — {repositoryFullName}");
		sb.AppendLine();
		sb.AppendLine($"{findings.Count} open {bandName} finding(s).");
		sb.AppendLine();

		if (findings.Count == 0)
		{
			return sb.ToString();
		}

		sb.AppendLine("Codacy grades the default branch, so these are resolved by fixing the code and");
		sb.AppendLine("pushing; the finding clears on Codacy's next analysis rather than immediately.");
		sb.AppendLine();

		var groups = findings
			.GroupBy(CategoryOf, StringComparer.OrdinalIgnoreCase)
			.OrderByDescending(group => group.Count())
			.ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

		foreach (var group in groups)
		{
			sb.AppendLine($"## {group.Key} ({group.Count()})");
			sb.AppendLine();

			foreach (var finding in group.OrderBy(f => f.DueAt).ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase))
			{
				var scan = string.IsNullOrWhiteSpace(finding.ScanType) ? string.Empty : $" [{finding.ScanType}]";
				var link = string.IsNullOrWhiteSpace(finding.HtmlUrl) ? string.Empty : $" — {finding.HtmlUrl}";
				sb.AppendLine($"- {finding.Title}{scan} ({finding.Status}, due {finding.DueAt:yyyy-MM-dd}){link}");
			}

			sb.AppendLine();
		}

		return sb.ToString();
	}

	/// <summary>
	/// Builds the structured data an AI remediation session and the UI both read.
	/// </summary>
	/// <param name="bandName">How this rule's severity band is named in prose.</param>
	/// <param name="findings">The findings in the band.</param>
	public static Dictionary<string, object> BuildAdvisoryData(
		string bandName,
		IReadOnlyList<CodacySecurityFinding> findings)
		=> new(StringComparer.Ordinal)
		{
			["band"] = bandName,
			["finding_count"] = findings.Count,
			["overdue_count"] = CountOverdue(findings),
			["categories"] = findings
				.GroupBy(CategoryOf, StringComparer.OrdinalIgnoreCase)
				.OrderByDescending(group => group.Count())
				.ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
			["scan_types"] = findings
				.Select(finding => finding.ScanType)
				.Where(scanType => !string.IsNullOrWhiteSpace(scanType))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(scanType => scanType, StringComparer.OrdinalIgnoreCase)
				.ToList()
		};

	private static int CountOverdue(IReadOnlyList<CodacySecurityFinding> findings)
		=> findings.Count(finding => string.Equals(finding.Status, "Overdue", StringComparison.OrdinalIgnoreCase));

	private static string CategoryOf(CodacySecurityFinding finding)
		=> string.IsNullOrWhiteSpace(finding.SecurityCategory) ? Uncategorised : finding.SecurityCategory!;
}
