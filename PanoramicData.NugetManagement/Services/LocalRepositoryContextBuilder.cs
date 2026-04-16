using Microsoft.Extensions.Logging;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Builds a <see cref="RepositoryContext"/> by reading files from the local filesystem.
/// Used after remediations have been applied locally so that re-assessment reflects
/// the on-disk state rather than the stale GitHub content.
/// </summary>
public class LocalRepositoryContextBuilder
{
	/// <summary>
	/// File extensions whose content is fetched for rule evaluation.
	/// </summary>
	private static readonly string[] _contentExtensions =
	[
		".csproj",
		".slnx",
		".yml",
		".yaml",
	];

	/// <summary>
	/// Root-level files whose content is always read (case-insensitive).
	/// </summary>
	private static readonly string[] _alwaysFetchFiles =
	[
		NugetManagementRepositoryConfig.FileName,
		".editorconfig",
		".gitignore",
		".codacy.yml",
		".codacy.yaml",
		".github/dependabot.yml",
		".github/dependabot.yaml",
		".github/workflows/ci.yml",
		"CONTRIBUTING.md",
		"Directory.Build.props",
		"Directory.Packages.props",
		"global.json",
		"LICENSE",
		"Publish.ps1",
		"README.md",
		"renovate.json",
		"SECURITY.md",
		"version.json"
	];

	private readonly ILogger<LocalRepositoryContextBuilder> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="LocalRepositoryContextBuilder"/> class.
	/// </summary>
	public LocalRepositoryContextBuilder(ILogger<LocalRepositoryContextBuilder> logger)
	{
		_logger = logger;
	}

	/// <summary>
	/// Builds a repository context from the local filesystem.
	/// </summary>
	/// <param name="localPath">Absolute path to the cloned repository root.</param>
	/// <param name="repositoryFullName">The GitHub full name (e.g. "owner/repo").</param>
	/// <param name="options">Per-repo assessment options.</param>
	/// <param name="defaultBranch">The default branch name (used for context only).</param>
	/// <param name="currentBranch">The currently checked out local branch when known.</param>
	/// <returns>A fully populated <see cref="RepositoryContext"/>.</returns>
	public RepositoryContext Build(
		string localPath,
		string repositoryFullName,
		RepoOptions options,
		string defaultBranch = "main",
		string? currentBranch = null)
	{
		var repoName = repositoryFullName.Contains('/')
			? repositoryFullName.Split('/')[1]
			: repositoryFullName;

		_logger.LogInformation("Building local context from {Path} for {FullName}", localPath, repositoryFullName);

		var filePaths = EnumerateRepositoryFiles(localPath);
		var fileContents = FetchFileContents(localPath, filePaths);
		fileContents.TryGetValue(NugetManagementRepositoryConfig.FileName, out var repoConfigRaw);
		var repositoryConfig = NugetManagementRepositoryConfigParser.Parse(repoConfigRaw);

		return new RepositoryContext
		{
			FullName = repositoryFullName,
			Name = repoName,
			DefaultBranch = defaultBranch,
			CurrentBranch = currentBranch,
			Options = options,
			FilePaths = filePaths,
			FileContents = fileContents,
			RepositoryConfig = repositoryConfig
		};
	}

	/// <summary>
	/// Enumerates all tracked files in the repository, excluding .git/ and common build output.
	/// Paths are returned with forward slashes relative to the repo root.
	/// </summary>
	private List<string> EnumerateRepositoryFiles(string localPath)
	{
		var files = new List<string>();

		try
		{
			foreach (var fullPath in Directory.EnumerateFiles(localPath, "*", SearchOption.AllDirectories))
			{
				var relative = Path.GetRelativePath(localPath, fullPath).Replace('\\', '/');

				// Skip .git directory and common build artifacts
				if (relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
					relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
					relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
					relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
					relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
					relative.Equals(".git", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				files.Add(relative);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to enumerate files in {Path}", localPath);
		}

		return files;
	}

	/// <summary>
	/// Reads content for files that rules need, matching the same selection logic
	/// as <see cref="RepositoryContextBuilder"/>.
	/// </summary>
	private Dictionary<string, string> FetchFileContents(string localPath, List<string> filePaths)
	{
		var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var filesToRead = DetermineFilesToFetch(filePaths);

		foreach (var relativePath in filesToRead)
		{
			var fullPath = Path.Combine(localPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
			try
			{
				if (File.Exists(fullPath))
				{
					contents[relativePath] = File.ReadAllText(fullPath);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to read {File}", relativePath);
			}
		}

		return contents;
	}

	/// <summary>
	/// Determines which files to read content for — mirrors <see cref="RepositoryContextBuilder"/>'s logic.
	/// </summary>
	private static List<string> DetermineFilesToFetch(List<string> filePaths)
	{
		var toFetch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Always fetch standard files
		foreach (var file in _alwaysFetchFiles)
		{
			if (filePaths.Contains(file, StringComparer.OrdinalIgnoreCase))
			{
				toFetch.Add(file);
			}
		}

		// Fetch files matching content extensions
		foreach (var path in filePaths)
		{
			foreach (var ext in _contentExtensions)
			{
				if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
				{
					toFetch.Add(path);
					break;
				}
			}
		}

		return [.. toFetch];
	}
}
