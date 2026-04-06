namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The complete assessment result for a single repository.
/// </summary>
public class RepoAssessment
{
	/// <summary>
	/// The GitHub repository full name (e.g. "panoramicdata/Highlight.Api").
	/// </summary>
	public required string RepositoryFullName { get; init; }

	/// <summary>
	/// The default branch name assessed.
	/// </summary>
	public required string DefaultBranch { get; init; }

	/// <summary>
	/// The time the assessment was performed (UTC).
	/// </summary>
	public required DateTimeOffset AssessedAtUtc { get; init; }

	/// <summary>
	/// The individual rule results for this repository.
	/// </summary>
	public required List<RuleResult> RuleResults { get; init; }

	/// <summary>
	/// The total number of rules that passed.
	/// </summary>
	public int PassedCount => RuleResults.Count(r => r.Passed);

	/// <summary>
	/// The total number of rules that failed.
	/// </summary>
	public int FailedCount => RuleResults.Count(r => !r.Passed);

	/// <summary>
	/// The number of critical failures (failed rules with Critical severity).
	/// </summary>
	public int CriticalCount => RuleResults.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Critical);

	/// <summary>
	/// The number of errors (failed rules with Error severity).
	/// </summary>
	public int ErrorCount => RuleResults.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Error);

	/// <summary>
	/// The number of warnings (failed rules with Warning severity).
	/// </summary>
	public int WarningCount => RuleResults.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Warning);

	/// <summary>
	/// The number of info findings (failed rules with Info severity).
	/// </summary>
	public int InfoCount => RuleResults.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Info);

	/// <summary>
	/// Whether the repository passes all Critical- and Error-severity rules.
	/// </summary>
	public bool IsCompliant => CriticalCount == 0 && ErrorCount == 0;
}
