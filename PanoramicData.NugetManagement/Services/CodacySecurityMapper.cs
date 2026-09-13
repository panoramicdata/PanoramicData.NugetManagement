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
		Status = item.Status.ToString(),
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
}
