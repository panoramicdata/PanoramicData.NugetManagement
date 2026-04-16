namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Context provided to rules for evaluating a repository.
/// Contains pre-fetched file contents from the repository.
/// </summary>
public class RepositoryContext
{
	/// <summary>
	/// The GitHub repository full name (e.g. "panoramicdata/Highlight.Api").
	/// </summary>
	public required string FullName { get; init; }

	/// <summary>
	/// The repository name (e.g. "Highlight.Api").
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// The default branch name.
	/// </summary>
	public required string DefaultBranch { get; init; }

	/// <summary>
	/// The currently checked out local branch when available.
	/// Null when context is built from remote-only repository metadata.
	/// </summary>
	public string? CurrentBranch { get; init; }

	/// <summary>
	/// The per-repo options (may be default).
	/// </summary>
	public required RepoOptions Options { get; init; }

	/// <summary>
	/// All file paths in the repository (relative).
	/// </summary>
	public required List<string> FilePaths { get; init; }

	/// <summary>
	/// Pre-fetched file contents keyed by relative path.
	/// Not all files will be fetched — only those relevant to assessment.
	/// </summary>
	public required Dictionary<string, string> FileContents { get; init; }

	/// <summary>
	/// Gets the content of a file, or null if not fetched.
	/// </summary>
	/// <param name="path">The relative file path.</param>
	/// <returns>The file content, or null if not present.</returns>
	public string? GetFileContent(string path)
		=> FileContents.TryGetValue(path, out var content) ? content : null;

	/// <summary>
	/// Whether a file exists in the repository.
	/// </summary>
	/// <param name="path">The relative file path.</param>
	/// <returns>True if the file path exists in the repository tree.</returns>
	public bool FileExists(string path)
		=> FilePaths.Contains(path, StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Finds all file paths matching a pattern (case-insensitive).
	/// </summary>
	/// <param name="suffix">The suffix to match (e.g. ".csproj").</param>
	/// <returns>The matching file paths.</returns>
	public IEnumerable<string> FindFiles(string suffix)
		=> FilePaths.Where(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
