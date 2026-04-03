using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Octokit;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Builds a <see cref="RepositoryContext"/> by fetching file metadata and content from GitHub.
/// <para>
/// File content is fetched via <c>raw.githubusercontent.com</c> instead of the GitHub REST API.
/// Raw content requests are served by GitHub's CDN and <strong>do not count</strong> against
/// the REST API rate limit (5,000 requests/hour for authenticated users). Only the recursive
/// tree call uses the REST API (1 call per repository).
/// </para>
/// </summary>
public class RepositoryContextBuilder : IDisposable
{
	/// <summary>
	/// Files that are always fetched because rules need their content.
	/// Paths are relative to the repo root.
	/// </summary>
	private static readonly string[] _alwaysFetchFiles =
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
		"Publish.ps1",
		"README.md",
		"renovate.json",
		"SECURITY.md",
		"version.json"
	];

	private readonly IGitHubClient _github;
	private readonly HttpClient _rawClient;
	private readonly bool _ownsHttpClient;
	private readonly ILogger<RepositoryContextBuilder> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="RepositoryContextBuilder"/> class.
	/// An internal <see cref="HttpClient"/> is created and configured with the token from
	/// <paramref name="github"/> for raw content fetching. Use the overload that accepts
	/// an <see cref="HttpClient"/> if you need to control the client lifetime yourself.
	/// </summary>
	/// <param name="github">The GitHub client.</param>
	/// <param name="logger">The logger.</param>
	public RepositoryContextBuilder(IGitHubClient github, ILogger<RepositoryContextBuilder> logger)
		: this(github, CreateDefaultRawClient(github), logger, ownsHttpClient: true)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="RepositoryContextBuilder"/> class
	/// with an externally-managed <see cref="HttpClient"/> for raw content requests.
	/// </summary>
	/// <param name="github">The GitHub client (used only for the recursive tree API call).</param>
	/// <param name="rawHttpClient">
	/// An <see cref="HttpClient"/> used to fetch raw file content from
	/// <c>raw.githubusercontent.com</c>. For private repositories the client should
	/// include an <c>Authorization</c> header with a valid token.
	/// </param>
	/// <param name="logger">The logger.</param>
	public RepositoryContextBuilder(IGitHubClient github, HttpClient rawHttpClient, ILogger<RepositoryContextBuilder> logger)
		: this(github, rawHttpClient, logger, ownsHttpClient: false)
	{
	}

	private RepositoryContextBuilder(IGitHubClient github, HttpClient rawHttpClient, ILogger<RepositoryContextBuilder> logger, bool ownsHttpClient)
	{
		_github = github;
		_rawClient = rawHttpClient;
		_logger = logger;
		_ownsHttpClient = ownsHttpClient;
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

		_logger.LogInformation("Fetching repository tree for {FullName} (1 API call)...", repository.FullName);

		// Get the full repository tree (1 REST API call)
		var filePaths = await GetFilePathsAsync(owner, repoName, defaultBranch).ConfigureAwait(false);

		// Determine which files to fetch content for
		var filesToFetch = DetermineFilesToFetch(filePaths);

		_logger.LogInformation(
			"Fetching {Count} file contents for {FullName} via raw.githubusercontent.com (0 API calls)...",
			filesToFetch.Count, repository.FullName);

		// Fetch file contents via raw CDN (does NOT consume API rate limit)
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

	/// <inheritdoc />
	public void Dispose()
	{
		if (_ownsHttpClient)
		{
			_rawClient.Dispose();
		}

		GC.SuppressFinalize(this);
	}

	private static HttpClient CreateDefaultRawClient(IGitHubClient github)
	{
		var client = new HttpClient
		{
			BaseAddress = new Uri("https://raw.githubusercontent.com/")
		};

		client.DefaultRequestHeaders.UserAgent.Add(
			new ProductInfoHeaderValue("PanoramicData.NugetManagement", "1.0"));

		// Propagate authentication for private repo access
		var credentials = github.Connection.Credentials;
		if (credentials?.AuthenticationType == AuthenticationType.Oauth)
		{
			client.DefaultRequestHeaders.Authorization =
				new AuthenticationHeaderValue("token", credentials.GetToken());
		}
		else if (credentials?.AuthenticationType == AuthenticationType.Bearer)
		{
			client.DefaultRequestHeaders.Authorization =
				new AuthenticationHeaderValue("Bearer", credentials.GetToken());
		}

		return client;
	}

	private async Task<List<string>> GetFilePathsAsync(string owner, string repoName, string branch)
	{
		try
		{
			var tree = await _github.Git.Tree.GetRecursive(owner, repoName, branch).ConfigureAwait(false);
			return [.. tree.Tree
				.Where(t => t.Type.Value == TreeType.Blob)
				.Select(t => t.Path)];
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
		foreach (var file in _alwaysFetchFiles)
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

		// Fetch all .slnx solution files
		foreach (var path in filePaths.Where(p => p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
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
				var fileContent = await GetRawFileContentAsync(owner, repoName, path, branch, cancellationToken).ConfigureAwait(false);
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

	private async Task<string?> GetRawFileContentAsync(
		string owner,
		string repoName,
		string path,
		string branch,
		CancellationToken cancellationToken)
	{
		// URL: https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}
		var url = $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repoName)}/{Uri.EscapeDataString(branch)}/{path}";

		try
		{
			var response = await _rawClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

			if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
			{
				_logger.LogDebug("File not found (raw): {Owner}/{Repo}/{Path}@{Branch}", owner, repoName, path, branch);
				return null;
			}

			response.EnsureSuccessStatusCode();
			return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			_logger.LogDebug("File not found (raw): {Owner}/{Repo}/{Path}@{Branch}", owner, repoName, path, branch);
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to fetch raw file: {Owner}/{Repo}/{Path}@{Branch}", owner, repoName, path, branch);
			return null;
		}
	}
}
