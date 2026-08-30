namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// One file's quality grade as reported by Codacy for a branch.
/// </summary>
/// <remarks>
/// A file's grade is not a restatement of its issue count: Codacy also folds duplication and
/// complexity into it, so a file with zero issues can still be graded F. That is exactly what made
/// the old combined gate unreadable — it reported "minimum file grade F, total issues 0" while the
/// Codacy issues page showed a clean repository.
/// </remarks>
public sealed class CodacyFileGrade
{
	/// <summary>The file's path within the repository.</summary>
	public required string Path { get; init; }

	/// <summary>
	/// The grade letter Codacy assigned (A-F), or null/blank for a file Codacy did not analyse.
	/// Markdown, JSON, images and the solution file all come back ungraded.
	/// </summary>
	public string? GradeLetter { get; init; }

	/// <summary>The numeric grade (0-100) behind the letter.</summary>
	public int Grade { get; init; }

	/// <summary>The number of open issues Codacy found in the file.</summary>
	public int TotalIssues { get; init; }

	/// <summary>The file's cyclomatic complexity, where Codacy measured one.</summary>
	public int? Complexity { get; init; }

	/// <summary>
	/// The percentage of the file Codacy considers duplicated, where it measured duplication. Often
	/// the whole reason for a poor grade on a file that has no issues at all.
	/// </summary>
	public int? Duplication { get; init; }

	/// <summary>The number of duplicated blocks Codacy found in the file.</summary>
	public int? NumberOfClones { get; init; }

	/// <summary>The file's line count, where Codacy measured one.</summary>
	public int? LinesOfCode { get; init; }

	/// <summary>
	/// Whether Codacy actually graded this file. An absent letter means "not analysed", which is not
	/// a grade and must never be read as one.
	/// </summary>
	public bool IsGraded => !string.IsNullOrWhiteSpace(GradeLetter);
}
