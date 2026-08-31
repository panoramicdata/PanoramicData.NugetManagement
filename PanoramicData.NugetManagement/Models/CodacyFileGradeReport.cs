namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The per-file grades Codacy holds for a single repository branch.
/// </summary>
public sealed class CodacyFileGradeReport
{
	/// <summary>
	/// Whether Codacy knows about the repository at all. When <see langword="false"/> the repository
	/// has never been added to Codacy, and Codacy answers the file listing with a 404.
	/// </summary>
	public required bool IsTracked { get; init; }

	/// <summary>Every file Codacy listed for the branch, graded or not.</summary>
	public IReadOnlyList<CodacyFileGrade> Files { get; init; } = [];

	/// <summary>
	/// The files Codacy actually analysed. A tracked repository with none of these has been added to
	/// Codacy but never scanned, which CQ-03 reports as a failure rather than a pass.
	/// </summary>
	public IEnumerable<CodacyFileGrade> GradedFiles => Files.Where(file => file.IsGraded);
}
