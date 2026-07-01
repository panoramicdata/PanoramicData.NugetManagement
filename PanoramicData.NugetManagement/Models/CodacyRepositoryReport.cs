namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The Codacy issue report for a single repository.
/// </summary>
public sealed class CodacyRepositoryReport
{
	/// <summary>
	/// Whether the repository is tracked/analysed by Codacy. When <see langword="false"/>, no issue
	/// data is available (the repository has not been added to Codacy or has not been analysed yet).
	/// </summary>
	public required bool IsTracked { get; init; }

	/// <summary>The open issues reported by Codacy. Empty when the repository is clean or untracked.</summary>
	public IReadOnlyList<CodacyIssue> Issues { get; init; } = [];
}
