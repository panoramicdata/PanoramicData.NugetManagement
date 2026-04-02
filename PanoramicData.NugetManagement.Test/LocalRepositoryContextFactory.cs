using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Helper that builds a <see cref="RepositoryContext"/> from the local filesystem.
/// Used for self-assessment integration tests and the FailArmy fixture.
/// </summary>
internal static class LocalRepositoryContextFactory
{
	/// <summary>
	/// Builds a RepositoryContext by scanning a local directory tree.
	/// </summary>
	/// <param name="rootPath">The absolute path to the repository root.</param>
	/// <param name="repoFullName">The GitHub-style full name (e.g. "panoramicdata/MyRepo").</param>
	/// <param name="options">Optional per-repo options.</param>
	/// <param name="excludedPathPrefixes">Optional relative path prefixes to exclude from the scan.</param>
	/// <returns>A RepositoryContext populated from the local filesystem.</returns>
	public static RepositoryContext Build(
		string rootPath,
		string repoFullName,
		RepoOptions? options = null,
		IEnumerable<string>? excludedPathPrefixes = null)
	{
		var repoOptions = options ?? new RepoOptions();
		var normalizedExcludedPrefixes = excludedPathPrefixes?
			.Select(prefix => prefix.Replace('\\', '/').TrimStart('/'))
			.ToList() ?? [];

		var allFiles = Directory
			.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
			.Select(f => Path.GetRelativePath(rootPath, f).Replace('\\', '/'))
			.Where(f => !f.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) &&
						!f.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
						!f.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
						!f.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
						!f.Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
						!normalizedExcludedPrefixes.Any(prefix => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
			.ToList();

		// Determine which files to read content for
		var filesToRead = allFiles
			.Where(ShouldReadContent)
			.ToList();

		var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var relativePath in filesToRead)
		{
			var fullPath = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
			if (File.Exists(fullPath))
			{
				fileContents[relativePath] = File.ReadAllText(fullPath);
			}
		}

		var repoName = repoFullName.Contains('/') ? repoFullName.Split('/')[1] : repoFullName;
		return new RepositoryContext
		{
			FullName = repoFullName,
			Name = repoName,
			DefaultBranch = "main",
			Options = repoOptions,
			FilePaths = allFiles,
			FileContents = fileContents
		};
	}

	private static bool ShouldReadContent(string path)
	{
		// Read common assessment-relevant files
		if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith(".gitignore", StringComparison.OrdinalIgnoreCase) ||
			path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		// Read specific files by name
		var fileName = Path.GetFileName(path);
		return fileName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase);
	}
}
