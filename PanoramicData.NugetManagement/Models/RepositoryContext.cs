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
	/// Optional repository-level project treatment configuration.
	/// </summary>
	public NugetManagementRepositoryConfig? RepositoryConfig { get; init; }

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
	{
		var matching = FilePaths.Where(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
		if (!suffix.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
		{
			return matching;
		}

		return matching.Where(ShouldIncludeProjectInAssessment);
	}

	/// <summary>
	/// Gets configured test projects after applying heuristics and project overrides.
	/// </summary>
	public IEnumerable<string> FindTestProjectFiles()
		=> FilePaths
			.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
			.Where(ShouldIncludeProjectInTesting);

	/// <summary>
	/// Gets non-test projects after applying project overrides.
	/// </summary>
	public IEnumerable<string> FindNonTestProjectFiles()
		=> FindFiles(".csproj").Where(path => !IsTestProject(path));

	/// <summary>
	/// Returns the single primary project for this repository: the .csproj whose
	/// filename (without extension) matches the repository name.
	/// Returns null if no such project exists (e.g. the repo is an app, not a library).
	/// </summary>
	public string? FindPrimaryProjectFile()
		=> FindNonTestProjectFiles()
			.FirstOrDefault(path =>
				string.Equals(
					Path.GetFileNameWithoutExtension(path),
					Name,
					StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Returns all non-test projects that are NOT the primary project.
	/// These are ancillary projects (tools, generators, etc.) that should not be published to NuGet.
	/// </summary>
	public IEnumerable<string> FindNonPrimaryNonTestProjectFiles()
	{
		var primary = FindPrimaryProjectFile();
		return FindNonTestProjectFiles()
			.Where(path => !string.Equals(path, primary, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Gets the project-level config for a path, if any.
	/// </summary>
	public NugetManagementProjectConfig? GetProjectConfig(string projectPath)
		=> RepositoryConfig?.GetProjectConfig(projectPath);

	/// <summary>
	/// Determines whether a project should be treated as a test project.
	/// </summary>
	public bool IsTestProject(string projectPath)
	{
		var projectConfig = GetProjectConfig(projectPath);
		return projectConfig?.TestingTreatment switch
		{
			ProjectTreatment.Include => true,
			ProjectTreatment.Exclude => false,
			_ => IsLikelyTestProject(projectPath)
		};
	}

	private bool ShouldIncludeProjectInAssessment(string projectPath)
	{
		var projectConfig = GetProjectConfig(projectPath);
		return projectConfig?.Treatment switch
		{
			ProjectTreatment.Include => true,
			ProjectTreatment.Exclude => false,
			_ => IsAutoIncludedProject(projectPath)
		};
	}

	private bool ShouldIncludeProjectInTesting(string projectPath)
	{
		var projectConfig = GetProjectConfig(projectPath);

		if (projectConfig?.TestingTreatment == ProjectTreatment.Include)
		{
			return true;
		}

		if (projectConfig?.TestingTreatment == ProjectTreatment.Exclude)
		{
			return false;
		}

		if (projectConfig?.Treatment == ProjectTreatment.Exclude)
		{
			return false;
		}

		return IsLikelyTestProject(projectPath) && IsAutoIncludedProject(projectPath);
	}

	private static bool IsLikelyTestProject(string projectPath)
	{
		var fileName = Path.GetFileName(projectPath);
		return fileName.Contains(".Test", StringComparison.OrdinalIgnoreCase)
			|| fileName.Contains(".Tests", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith("Tests.csproj", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith("Test.csproj", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsAutoIncludedProject(string projectPath)
		=> !projectPath.Contains("/Fixtures/", StringComparison.OrdinalIgnoreCase)
			&& !projectPath.Contains("/TestData/", StringComparison.OrdinalIgnoreCase)
			&& !projectPath.Contains("/Samples/", StringComparison.OrdinalIgnoreCase);
}
