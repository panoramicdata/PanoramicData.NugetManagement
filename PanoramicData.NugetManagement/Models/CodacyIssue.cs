namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// A single code-quality issue reported by Codacy for a repository.
/// </summary>
public sealed class CodacyIssue
{
	/// <summary>The repository-relative file path the issue was found in.</summary>
	public required string FilePath { get; init; }

	/// <summary>The 1-based line number, or 0 when not applicable.</summary>
	public long Line { get; init; }

	/// <summary>The human-readable issue message.</summary>
	public required string Message { get; init; }

	/// <summary>The Codacy pattern identifier (e.g. "SonarCSharp_S2360").</summary>
	public string? PatternId { get; init; }

	/// <summary>The pattern category (e.g. "BestPractice", "Security").</summary>
	public string? Category { get; init; }

	/// <summary>The Codacy severity level (e.g. "Info", "Warning", "Error").</summary>
	public string? Severity { get; init; }

	/// <summary>The detected language (e.g. "CSharp", "Markdown").</summary>
	public string? Language { get; init; }
}
