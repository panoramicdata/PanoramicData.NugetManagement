namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Optional Codacy quality gate settings for a repository.
/// </summary>
public class CodacyOptions
{
	/// <summary>
	/// Codacy API token used to query repository quality data.
	/// </summary>
	public required string ApiToken { get; set; }

	/// <summary>
	/// Minimum acceptable Codacy grade level.
	/// </summary>
	public CodacyLevel MinimumLevel { get; set; } = CodacyLevel.A;

	/// <summary>
	/// Maximum acceptable number of issues.
	/// </summary>
	public int MaxIssueCount { get; set; }
}
