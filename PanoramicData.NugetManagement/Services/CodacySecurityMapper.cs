using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Turns a Codacy SRM item into the finding the SEC rules read.
/// </summary>
/// <remarks>
/// The one place the client library's shape is allowed to reach, so a Codacy.Api upgrade that
/// renames a field fails here rather than in three rules.
/// </remarks>
internal static class CodacySecurityMapper
{
	/// <summary>
	/// Maps one SRM item.
	/// </summary>
	public static CodacySecurityFinding Map(SrmItem item) => new()
	{
		Id = item.Id,
		Title = item.Title,
		Priority = MapPriority(item.Priority),
		SlaStatus = MapSlaStatus(item.Status),
		SecurityCategory = item.SecurityCategory,
		ScanType = item.ScanType,
		HtmlUrl = item.HtmlUrl,
		OpenedAt = item.OpenedAt,
		DueAt = item.DueAt
	};

	/// <summary>
	/// Maps Codacy's priority onto ours. Deliberately exhaustive: a priority Codacy adds later must
	/// fail loudly here rather than fall into SEC-03 and be graded blue.
	/// </summary>
	private static CodacySecurityPriority MapPriority(SrmPriority priority) => priority switch
	{
		SrmPriority.Critical => CodacySecurityPriority.Critical,
		SrmPriority.High => CodacySecurityPriority.High,
		SrmPriority.Medium => CodacySecurityPriority.Medium,
		SrmPriority.Low => CodacySecurityPriority.Low,
		_ => throw new ArgumentOutOfRangeException(
			nameof(priority),
			priority,
			"Codacy reported a security priority this build does not know how to grade.")
	};

	/// <summary>
	/// Maps Codacy's SLA status onto ours. Exhaustive for the same reason as the priority: a status
	/// quietly treated as on track would bury an overdue finding at the bottom of the advisory.
	/// </summary>
	/// <remarks>
	/// The closed and ignored statuses are unreachable here — the search asks only for open ones —
	/// so they throw alongside anything Codacy adds later rather than being mapped to a fiction.
	/// </remarks>
	private static CodacySecuritySlaStatus MapSlaStatus(SrmStatus status) => status switch
	{
		SrmStatus.Overdue => CodacySecuritySlaStatus.Overdue,
		SrmStatus.DueSoon => CodacySecuritySlaStatus.DueSoon,
		SrmStatus.OnTrack => CodacySecuritySlaStatus.OnTrack,
		_ => throw new ArgumentOutOfRangeException(
			nameof(status),
			status,
			"Codacy reported a security SLA status this build does not know how to group.")
	};
}
