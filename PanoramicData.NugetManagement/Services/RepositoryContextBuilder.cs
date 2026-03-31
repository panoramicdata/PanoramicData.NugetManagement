using Microsoft.Extensions.Logging;
using Octokit;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Builds a <see cref="RepositoryContext"/> by fetching file metadata and content from GitHub.
/// </summary>
public class RepositoryContextBuilder
{
	/// <summary>
	/// Files that are always fetched because rules need their content.
	/// Paths are relative to the repo root.
	/// </summary>
	private static readonly string[] AlwaysFetchFiles =
	[
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
		"README.md",
		"renovate.json",
		"SECURITY.md",
		"version.json"
	];

	private readonly IGitHubClient _github;
	private readonly ILogger<RepositoryContextBuilder> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="RepositoryContextBuilder"/> class.
	/// </summary>
	/// <param name="github">The GitHub client.</param>
	/// <param name="logger">The logger.</param>
	public RepositoryContextBuilder(IGitHubClient github, ILogger<RepositoryContextBuilder> logger)
	{
		_github = github;
		_logger = logger;
	}

	/// <summary>
	/// Builds a repository context for assessment.
	/// </summary>
	/// <param name="repository">The GitHub repository.</param>
	/// <param name="options">The per-repo options.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>A fully populated RepositoryContext.</returns>
	public async Task<RepositoryContext> BuildAsync(
		Repository repository,
		RepoOptions options,
		CancellationToken cancellationToken = default)
	{
		var owner = repository.Owner.Login;
		var repoName = repository.Name;
		var defaultBranch = repository.DefaultBranch ?? "main";

		_logger.LogInformation("Fetching repository tree for {FullName}...", repository.FullName);

		// Get the full repository tree
		var filePaths = await GetFilePathsAsync(owner, repoName, defaultBranch).ConfigureAwait(false);

		// Determine which files to fetch content for
		var filesToFetch = DetermineFilesToFetch(filePaths);

		_logger.LogInformation("Fetching {Count} file contents for {FullName}...", filesToFetch.Count, repository.FullName);

		// Fetch file contents in parallel
		var fileContents = await FetchFileContentsAsync(owner, repoName, defaultBranch, filesToFetch, cancellationToken).ConfigureAwait(false);

		return new RepositoryContext
		{
			FullName = repository.FullName,
			Name = repoName,
			DefaultBranch = defaultBranch,
			Options = options,
			FilePaths = filePaths,
			FileContents = fileContents
		};
	}

	private async Task<List<string>> GetFilePathsAsync(string owner, string repoName, string branch)
	{
		try
		{
			var tree = await _github.Git.Tree.GetRecursive(owner, repoName, branch).ConfigureAwait(false);
			return tree.Tree
				.Where(t => t.Type.Value == TreeType.Blob)
				.Select(t => t.Path)
				.ToList();
		}
		catch (NotFoundException)
		{
			_logger.LogWarning("Repository tree not found for {Owner}/{Repo}@{Branch}. Empty repo?", owner, repoName, branch);
			return [];
		}
	}

	private static List<string> DetermineFilesToFetch(List<string> filePaths)
	{
		var toFetch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Always fetch the standard files if they exist
		foreach (var file in AlwaysFetchFiles)
		{
			if (filePaths.Contains(file, StringComparer.OrdinalIgnoreCase))
			{
				toFetch.Add(file);
			}
		}

		// Fetch all .csproj files
		foreach (var path in filePaths.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
		{
			toFetch.Add(path);
		}

		// Fetch all workflow files
		foreach (var path in filePaths.Where(p =>
			p.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) &&
			(p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))))
		{
			toFetch.Add(path);
		}

		return [.. toFetch];
	}

	private async Task<Dictionary<string, string>> FetchFileContentsAsync(
		string owner,
		string repoName,
		string branch,
		List<string> filePaths,
		CancellationToken cancellationToken)
	{
		var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var semaphore = new SemaphoreSlim(10);

		var tasks = filePaths.Select(async path =>
		{
			await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var fileContent = await GetFileContentAsync(owner, repoName, path, branch).ConfigureAwait(false);
				if (fileContent is not null)
				{
					lock (contents)
					{
						contents[path] = fileContent;
					}
				}
			}
			finally
			{
				semaphore.Release();
			}
		});

		await Task.WhenAll(tasks).ConfigureAwait(false);
		return contents;
	}

	private async Task<string?> GetFileContentAsync(string owner, string repoName, string path, string branch)
	{
		try
		{
			var contents = await _github.Repository.Content.GetAllContentsByRef(owner, repoName, path, branch).ConfigureAwait(false);
			return contents.FirstOrDefault()?.Content;
		}
		catch (NotFoundException)
		{
			_logger.LogDebug("File not found: {Owner}/{Repo}/{Path}@{Branch}", owner, repoName, path, branch);
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to fetch file: {Owner}/{Repo}/{Path}@{Branch}", owner, repoName, path, branch);
			return null;
		}
	}
}
