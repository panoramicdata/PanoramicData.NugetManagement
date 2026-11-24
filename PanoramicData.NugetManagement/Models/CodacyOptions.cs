namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Optional Codacy quality gate settings for a repository.
/// </summary>
public class CodacyOptions
{
	/// <summary>
	/// Codacy API token used to query repository quality data. Optional: when omitted, the
	/// organization-level token from <see cref="AssessmentOptions.CodacyApiToken"/> is used, allowing
	/// a repository to override only the thresholds below.
	/// </summary>
	public string? ApiToken { get; set; }

	/// <summary>
	/// Minimum acceptable Codacy grade level.
	/// </summary>
	public CodacyLevel MinimumLevel { get; set; } = CodacyLevel.A;

	/// <summary>
	/// Maximum acceptable number of issues.
	/// </summary>
	public int MaxIssueCount { get; set; }

	/// <summary>
	/// Age in days after which the repository's CODACY_PROJECT_TOKEN secret is reported by CQ-09 as
	/// due for replacement. Codacy project tokens last a year; the default leaves about a month.
	/// </summary>
	public int TokenWarnAfterDays { get; set; } = 335;
}
