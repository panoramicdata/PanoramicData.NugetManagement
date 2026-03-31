namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The aggregated assessment result for an entire GitHub organization.
/// </summary>
public class OrganizationAssessmentResult
{
	/// <summary>
	/// The GitHub organization name.
	/// </summary>
	public required string OrganizationName { get; init; }

	/// <summary>
	/// The time the assessment was performed (UTC).
	/// </summary>
	public required DateTimeOffset AssessedAtUtc { get; init; }

	/// <summary>
	/// The individual repository assessment results.
	/// </summary>
	public required List<RepoAssessment> RepositoryAssessments { get; init; }

	/// <summary>
	/// The total number of repositories assessed.
	/// </summary>
	public int RepositoryCount => RepositoryAssessments.Count;

	/// <summary>
	/// The number of repositories that are fully compliant (zero errors).
	/// </summary>
	public int CompliantCount => RepositoryAssessments.Count(r => r.IsCompliant);

	/// <summary>
	/// The number of repositories that have at least one error.
	/// </summary>
	public int NonCompliantCount => RepositoryAssessments.Count(r => !r.IsCompliant);
}
