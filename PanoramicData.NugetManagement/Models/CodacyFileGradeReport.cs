namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The per-file grades Codacy holds for a single repository branch.
/// </summary>
public sealed class CodacyFileGradeReport
{
	/// <summary>
	/// Whether Codacy knows about the repository at all. <see langword="false"/> only when Codacy's
	/// repository endpoint answers 404 for the name as well, which is the one answer that establishes
	/// the repository was never added.
	/// </summary>
	/// <remarks>
	/// The file listing's own 404 is not enough. Codacy answered it for eleven repositories in one
	/// sweep, six of which it demonstrably held — added days earlier, default branch enabled — and
	/// reading that as absence had CQ-03 telling the reader to add a repository whose Codacy
	/// dashboard was already open.
	/// </remarks>
	public required bool IsTracked { get; init; }

	/// <summary>Every file Codacy listed for the branch, graded or not.</summary>
	public IReadOnlyList<CodacyFileGrade> Files { get; init; } = [];

	/// <summary>
	/// The files Codacy actually analysed. A tracked repository with none of these has been added to
	/// Codacy but never scanned, which CQ-03 reports as a failure rather than a pass.
	/// </summary>
	public IEnumerable<CodacyFileGrade> GradedFiles => Files.Where(file => file.IsGraded);
}
