namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The Codacy issue report for a single repository.
/// </summary>
public sealed class CodacyRepositoryReport
{
	/// <summary>
	/// Whether Codacy holds the repository. <see langword="false"/> only when Codacy's repository
	/// endpoint answers 404 for the name, i.e. it was never added; a repository that is added but
	/// unanalysed is tracked with no issues, because a search 404 does not tell the two apart.
	/// </summary>
	public required bool IsTracked { get; init; }

	/// <summary>The open issues reported by Codacy. Empty when the repository is clean or untracked.</summary>
	public IReadOnlyList<CodacyIssue> Issues { get; init; } = [];

	/// <summary>
	/// Where Codacy's analysis of the branch had got to, or null when that could not be established. The issues
	/// describe the commit named here, which need not be the one checked out.
	/// </summary>
	public CodacyAnalysisState? AnalysisState { get; init; }
}
