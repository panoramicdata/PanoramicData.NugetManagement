using System.Diagnostics;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Service for executing git and dotnet CLI commands on local repositories.
/// </summary>
public class LocalRepoService
{
	private readonly AppSettings _settings;
	private readonly ILogger<LocalRepoService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="LocalRepoService"/> class.
	/// </summary>
	public LocalRepoService(IOptions<AppSettings> settings, ILogger<LocalRepoService> logger)
	{
		_settings = settings.Value;
		_logger = logger;
	}

	/// <summary>
	/// Gets the root directory where repos are expected to be cloned as siblings.
	/// Walks up from the current directory to find the .git folder (this solution's
	/// own repository), then returns its parent — which is the org-level directory
	/// containing all sibling repos.
	/// </summary>
	public string GetReposRoot()
	{
		if (_settings.LocalReposRoot is not null)
		{
			return _settings.LocalReposRoot;
		}

		// Walk up from the current working directory to find a .git folder.
		// The directory containing .git is the solution's repo root;
		// its parent is where sibling repos live.
		var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
		while (dir is not null)
		{
			if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
			{
				return dir.Parent?.FullName ?? dir.FullName;
			}

			dir = dir.Parent;
		}

		// Fallback: parent of the current directory
		return Path.GetDirectoryName(Directory.GetCurrentDirectory())
			?? Directory.GetCurrentDirectory();
	}

	/// <summary>
	/// Gets the local path for a repository by name.
	/// </summary>
	public string GetLocalPath(string repoName)
		=> Path.Combine(GetReposRoot(), repoName);

	/// <summary>
	/// Checks if a repository is cloned locally.
	/// </summary>
	public bool IsClonedLocally(string repoName)
	{
		var path = GetLocalPath(repoName);
		return Directory.Exists(Path.Combine(path, ".git"));
	}

	/// <summary>
	/// Finds the first .slnx file in a local repository, or null if none exists.
	/// </summary>
	public string? FindSlnxFile(string repoName)
	{
		var path = GetLocalPath(repoName);
		if (!Directory.Exists(path))
		{
			return null;
		}

		try
		{
			return Directory.EnumerateFiles(path, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to search for .slnx in {Path}", path);
			return null;
		}
	}

	/// <summary>
	/// Gets the current branch name for a local repository.
	/// </summary>
	public async Task<string?> GetCurrentBranchAsync(string repoName, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoName);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", "rev-parse --abbrev-ref HEAD", cancellationToken).ConfigureAwait(false);
		return exitCode == 0 ? output.Trim() : null;
	}

	/// <summary>
	/// Checks whether the working tree is clean.
	/// </summary>
	public async Task<bool> IsWorkingTreeCleanAsync(string repoName, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoName);
		var (exitCode, output) = await RunCommandAsync(path, "git", "status --porcelain", cancellationToken).ConfigureAwait(false);
		return exitCode == 0 && string.IsNullOrWhiteSpace(output);
	}

	/// <summary>
	/// Checks whether the local branch is in sync with its origin counterpart.
	/// Performs a git fetch first, then compares HEAD against origin/{branch}.
	/// Returns true if HEAD matches origin/{branch} (not behind and not ahead).
	/// </summary>
	public async Task<bool?> IsSyncedWithOriginAsync(string repoName, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoName);
		if (!Directory.Exists(path))
		{
			return null;
		}

		// Fetch latest from origin
		var (fetchExit, _) = await RunCommandAsync(path, "git", "fetch --prune", cancellationToken).ConfigureAwait(false);
		if (fetchExit != 0)
		{
			return null;
		}

		// Get current branch
		var (branchExit, branchOutput) = await RunCommandAsync(path, "git", "rev-parse --abbrev-ref HEAD", cancellationToken).ConfigureAwait(false);
		if (branchExit != 0)
		{
			return null;
		}

		var branch = branchOutput.Trim();

		// Check if behind origin
		var (behindExit, behindOutput) = await RunCommandAsync(path, "git", $"rev-list --count HEAD..origin/{branch}", cancellationToken).ConfigureAwait(false);
		if (behindExit != 0)
		{
			// Remote tracking branch may not exist
			return null;
		}

		// Check if ahead of origin
		var (aheadExit, aheadOutput) = await RunCommandAsync(path, "git", $"rev-list --count origin/{branch}..HEAD", cancellationToken).ConfigureAwait(false);
		if (aheadExit != 0)
		{
			return null;
		}

		var behind = int.TryParse(behindOutput.Trim(), out var b) ? b : -1;
		var ahead = int.TryParse(aheadOutput.Trim(), out var a) ? a : -1;

		return behind == 0 && ahead == 0;
	}

	/// <summary>
	/// Clones a repository from GitHub.
	/// </summary>
	public async Task<(bool Success, string Output)> CloneAsync(
		string cloneUrl,
		string repoName,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var targetPath = GetLocalPath(repoName);
		_logger.LogInformation("Cloning {Url} to {Path}", cloneUrl, targetPath);
		return await RunCommandWithStreamingAsync(GetReposRoot(), "git", $"clone {cloneUrl} {repoName}", onOutput, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Creates a branch, commits changes, and pushes.
	/// </summary>
	public async Task<(bool Success, string Output)> CreateBranchCommitPushAsync(
		string repoName,
		string branchName,
		string commitMessage,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoName);

		// Create and checkout branch
		var (ok, output) = await RunCommandWithStreamingAsync(path, "git", $"checkout -b {branchName}", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Stage all changes
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", "add -A", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Commit
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", $"commit -m \"{commitMessage}\"", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Push
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", $"push -u origin {branchName}", onOutput, cancellationToken).ConfigureAwait(false);
		return (ok, output);
	}

	/// <summary>
	/// Checks out main and merges a branch.
	/// </summary>
	public async Task<(bool Success, string Output)> MergeToMainAsync(
		string repoName,
		string branchName,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoName);

		var (ok, output) = await RunCommandWithStreamingAsync(path, "git", "checkout main", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		(ok, output) = await RunCommandWithStreamingAsync(path, "git", $"merge {branchName}", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		(ok, output) = await RunCommandWithStreamingAsync(path, "git", "push origin main", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Clean up branch
		await RunCommandWithStreamingAsync(path, "git", $"branch -d {branchName}", onOutput, cancellationToken).ConfigureAwait(false);
		await RunCommandWithStreamingAsync(path, "git", $"push origin --delete {branchName}", onOutput, cancellationToken).ConfigureAwait(false);

		return (true, output);
	}

	/// <summary>
	/// Runs dotnet build on the repository.
	/// </summary>
	public async Task<(bool Success, string Output)> BuildAsync(
		string repoName,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoName);
		_logger.LogInformation("Building in {Path}", path);
		return await RunCommandWithStreamingAsync(path, "dotnet", "build --no-restore --verbosity normal", onOutput, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Syncs a local repository with its remote: fetch, pull (rebase), push.
	/// </summary>
	public async Task<(bool Success, string Output)> GitSyncAsync(
		string repoName,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoName);
		_logger.LogInformation("Git syncing {Path}", path);

		// Fetch
		var (ok, output) = await RunCommandWithStreamingAsync(path, "git", "fetch --prune", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Pull with rebase
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", "pull --rebase", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Push
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", "push", onOutput, cancellationToken).ConfigureAwait(false);
		return (ok, output);
	}

	/// <summary>
	/// Runs dotnet test on the repository.
	/// </summary>
	public async Task<(bool Success, string Output)> RunTestsAsync(
		string repoName,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoName);
		_logger.LogInformation("Running tests in {Path}", path);
		return await RunCommandWithStreamingAsync(path, "dotnet", "test --no-restore --verbosity normal", onOutput, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs the Publish.ps1 script.
	/// </summary>
	public async Task<(bool Success, string Output)> RunPublishScriptAsync(
		string repoName,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoName);
		var publishScript = Path.Combine(path, "Publish.ps1");
		if (!File.Exists(publishScript))
		{
			return (false, "Publish.ps1 not found.");
		}

		_logger.LogInformation("Running Publish.ps1 in {Path}", path);
		return await RunCommandWithStreamingAsync(path, "pwsh", "-ExecutionPolicy Bypass -File Publish.ps1", onOutput, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs a command and returns exit code + output.
	/// </summary>
	public static async Task<(int ExitCode, string Output)> RunCommandAsync(
		string workingDirectory,
		string fileName,
		string arguments,
		CancellationToken cancellationToken = default)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
				WorkingDirectory = workingDirectory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};

		process.Start();
		var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

		return (process.ExitCode, string.IsNullOrEmpty(error) ? output : $"{output}\n{error}");
	}

	/// <summary>
	/// Runs a command with streaming output via callback.
	/// </summary>
	public static async Task<(bool Success, string Output)> RunCommandWithStreamingAsync(
		string workingDirectory,
		string fileName,
		string arguments,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
				WorkingDirectory = workingDirectory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};

		var outputLines = new List<string>();

		process.OutputDataReceived += (_, e) =>
		{
			if (e.Data is not null)
			{
				outputLines.Add(e.Data);
				onOutput?.Invoke(e.Data);
			}
		};

		process.ErrorDataReceived += (_, e) =>
		{
			if (e.Data is not null)
			{
				outputLines.Add(e.Data);
				onOutput?.Invoke(e.Data);
			}
		};

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

		var fullOutput = string.Join('\n', outputLines);
		return (process.ExitCode == 0, fullOutput);
	}
}
