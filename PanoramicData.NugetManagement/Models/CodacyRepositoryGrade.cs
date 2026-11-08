namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Codacy's repository-level grade for one branch.
/// </summary>
public sealed class CodacyRepositoryGrade
{
	/// <summary>
	/// Whether Codacy holds the repository at all.
	/// </summary>
	public bool IsTracked { get; init; }

	/// <summary>
	/// False when Codacy holds the repository but has no analysis of the branch asked about.
	/// </summary>
	public bool IsBranchAnalysed { get; init; } = true;

	/// <summary>
	/// The branch Codacy itself treats as default, when known.
	/// </summary>
	public string? CodacyDefaultBranch { get; init; }

	/// <summary>
	/// The grade, 0 to 100, or null when Codacy has not graded the branch yet.
	/// </summary>
	public int? Grade { get; init; }

	/// <summary>
	/// The grade letter, A to F, or null when Codacy has not graded the branch yet.
	/// </summary>
	public string? GradeLetter { get; init; }

	/// <summary>
	/// The percentage of lines with issues.
	/// </summary>
	public long? IssuesPercentage { get; init; }

	/// <summary>
	/// The percentage of files over the complexity threshold.
	/// </summary>
	public long? ComplexFilesPercentage { get; init; }

	/// <summary>
	/// The percentage of duplicated files.
	/// </summary>
	public long? DuplicationPercentage { get; init; }

	/// <summary>
	/// Which commit the grade describes, when known.
	/// </summary>
	public CodacyAnalysisState? AnalysisState { get; init; }
}
