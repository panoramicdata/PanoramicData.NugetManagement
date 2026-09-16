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

		return $"Codacy reports {findings.Count} open {bandName} security finding(s): "
			+ $"{string.Join(", ", byCategory)}.{BuildSlaNote(findings)}";
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

		// SLA first, category second. A reader triaging a long list wants the late work at the top;
		// which kind of problem it is only matters once they have decided what to pick up.
		var slaGroups = findings
			.GroupBy(finding => finding.SlaStatus)
			.OrderBy(group => group.Key);

		foreach (var slaGroup in slaGroups)
		{
			sb.AppendLine($"## {LabelOf(slaGroup.Key)} ({slaGroup.Count()})");
			sb.AppendLine();

			var categoryGroups = slaGroup
				.GroupBy(CategoryOf, StringComparer.OrdinalIgnoreCase)
				.OrderByDescending(group => group.Count())
				.ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

			foreach (var categoryGroup in categoryGroups)
			{
				sb.AppendLine($"### {categoryGroup.Key} ({categoryGroup.Count()})");
				sb.AppendLine();

				var ordered = categoryGroup
					.OrderBy(f => f.DueAt)
					.ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase);

				foreach (var finding in ordered)
				{
					var scan = string.IsNullOrWhiteSpace(finding.ScanType) ? string.Empty : $" [{finding.ScanType}]";
					var link = string.IsNullOrWhiteSpace(finding.HtmlUrl) ? string.Empty : $" — {finding.HtmlUrl}";

					// No status on the bullet: the heading it sits under already says it.
					sb.AppendLine($"- {finding.Title}{scan} (due {finding.DueAt:yyyy-MM-dd}){link}");
				}

				sb.AppendLine();
			}
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
			["overdue_count"] = CountOf(findings, CodacySecuritySlaStatus.Overdue),
			["due_soon_count"] = CountOf(findings, CodacySecuritySlaStatus.DueSoon),
			["on_track_count"] = CountOf(findings, CodacySecuritySlaStatus.OnTrack),
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

	/// <summary>
	/// The SLA clause appended to the summary: the two statuses worth acting on, and nothing at all
	/// when every finding is on track — a trailing "0 overdue" would be noise on every clean band.
	/// </summary>
	private static string BuildSlaNote(IReadOnlyList<CodacySecurityFinding> findings)
	{
		var clauses = new List<string>(2);

		var overdue = CountOf(findings, CodacySecuritySlaStatus.Overdue);
		if (overdue > 0)
		{
			clauses.Add($"{overdue} overdue");
		}

		var dueSoon = CountOf(findings, CodacySecuritySlaStatus.DueSoon);
		if (dueSoon > 0)
		{
			clauses.Add($"{dueSoon} due soon");
		}

		return clauses.Count == 0 ? string.Empty : $" {string.Join(", ", clauses)}.";
	}

	/// <summary>How a status is named in prose, which is not how the enum spells it.</summary>
	private static string LabelOf(CodacySecuritySlaStatus status) => status switch
	{
		CodacySecuritySlaStatus.Overdue => "Overdue",
		CodacySecuritySlaStatus.DueSoon => "Due soon",
		CodacySecuritySlaStatus.OnTrack => "On track",
		_ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown SLA status.")
	};

	private static int CountOf(IReadOnlyList<CodacySecurityFinding> findings, CodacySecuritySlaStatus status)
		=> findings.Count(finding => finding.SlaStatus == status);

	private static string CategoryOf(CodacySecurityFinding finding)
		=> string.IsNullOrWhiteSpace(finding.SecurityCategory) ? Uncategorised : finding.SecurityCategory!;
}
