using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Service for executing git and dotnet CLI commands on local repositories.
/// </summary>
public partial class LocalRepoService
{
	/// <summary>
	/// Matches ANSI/VT escape sequences (e.g. colour codes like <c>ESC[31;1m</c>) that
	/// console tools such as pwsh emit. These render as gibberish in the dashboard's
	/// plain-text console, so they are stripped from captured output.
	/// </summary>
	[GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
	private static partial Regex AnsiEscapeRegex();

	/// <summary>
	/// Removes ANSI/VT escape sequences from a line of captured process output.
	/// </summary>
	private static string StripAnsi(string line) => AnsiEscapeRegex().Replace(line, string.Empty);

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
	/// The app-owned clone directory, created alongside this application's own repository.
	/// </summary>
	private const string DefaultReposFolderName = ".nugetmanagement-repos";

	/// <summary>
	/// Gets the root directory the app clones repositories into.
	/// </summary>
	/// <remarks>
	/// This is deliberately a directory of the app's own, and not the directory that holds the user's
	/// working copies. It used to be the latter: the walk below found this application's repository and
	/// returned its <em>parent</em>, so every clone, sync, commit and push the app performed happened
	/// inside whatever the user had checked out there. Since <see cref="CommitAndPushAsync"/> stages with
	/// <c>git add -A</c>, a bulk fix could commit and push the user's uncommitted work along with its own
	/// changes, on whatever branch they happened to be on. Cloning into the app's own root keeps the two
	/// apart, and lets clones be qualified by owner (see <see cref="GetLocalPath"/>) without dictating how
	/// the user arranges their own repositories.
	/// </remarks>
	public string GetReposRoot()
	{
		if (_settings.LocalReposRoot is not null)
		{
			return _settings.LocalReposRoot;
		}

		// Walk up from the current working directory to find a .git folder: the directory containing it
		// is this application's own repository, and the app's clone root sits beside it — near the user's
		// code (so paths stay short and on the same volume) but plainly not one of their repositories.
		var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
		while (dir is not null)
		{
			if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
			{
				return Path.Combine(dir.Parent?.FullName ?? dir.FullName, DefaultReposFolderName);
			}

			dir = dir.Parent;
		}

		// Published or installed, with no repository above us: fall back to per-user application data.
		return Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"PanoramicData.NugetManagement",
			"repos");
	}

	/// <summary>
	/// Gets the directory the user keeps their own clones in — the one this application's repository
	/// sits in alongside them — or null when that cannot be established.
	/// </summary>
	/// <remarks>
	/// The app never writes here. It is read to offer a shortcut: a repository already on this machine
	/// can be copied into the app's own root instead of fetched again. See
	/// <see cref="FindLocalRepositoriesAsync"/>.
	/// </remarks>
	public static string? GetUserCodeRoot()
	{
		var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
		while (dir is not null)
		{
			if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
			{
				return dir.Parent?.FullName;
			}

			dir = dir.Parent;
		}

		return null;
	}

	/// <summary>
	/// Finds git checkouts directly inside the user's code directory, paired with the <c>owner/name</c>
	/// their <c>origin</c> remote actually points at.
	/// </summary>
	/// <remarks>
	/// Keyed on the remote rather than the folder name, because a folder can be named anything and the
	/// caller is about to decide which organisation a checkout belongs to. Folders with no readable
	/// origin are left out: there is nothing to match them against, and guessing from the name is how
	/// the wrong repository gets written to. The app's own clone root is skipped so its copies are never
	/// offered back to it as sources.
	/// </remarks>
	public async Task<IReadOnlyList<(string Path, string RepoIdentity)>> FindLocalRepositoriesAsync(
		CancellationToken cancellationToken = default)
	{
		var codeRoot = GetUserCodeRoot();
		if (codeRoot is null || !Directory.Exists(codeRoot))
		{
			return [];
		}

		var appReposRoot = GetReposRoot();
		var found = new List<(string Path, string RepoIdentity)>();

		foreach (var path in Directory.EnumerateDirectories(codeRoot))
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (!Directory.Exists(Path.Combine(path, ".git"))
				|| path.StartsWith(appReposRoot, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var (exitCode, output) = await RunCommandAsync(path, "git", "remote get-url origin", cancellationToken)
				.ConfigureAwait(false);
			if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
			{
				continue;
			}

			var identity = ParseRepoIdentity(output.Trim());
			if (identity is null)
			{
				_logger.LogDebug("Skipping {Path}: origin {Origin} is not a GitHub repository", path, output.Trim());
				continue;
			}

			found.Add((path, identity));
		}

		_logger.LogInformation(
			"Found {Count} GitHub checkout(s) in {Root}: {Identities}",
			found.Count,
			codeRoot,
			string.Join(", ", found.Select(f => f.RepoIdentity)));

		return found;
	}

	/// <summary>
	/// Reduces a GitHub remote URL to <c>owner/name</c>, or null if it is not one. Handles the https and
	/// ssh forms, with or without a trailing <c>.git</c>.
	/// </summary>
	/// <remarks>
	/// The host has to be github.com, and that is the point rather than a detail. Without the check, an
	/// <c>owner/name</c> is happily extracted from remotes that have nothing to do with GitHub — a
	/// bitbucket.org URL under the same organisation name yields exactly the identity a GitHub repository
	/// of that name would, and an Azure DevOps path yields a plausible-looking pair of its last two
	/// segments. Both are present in real code directories. Since callers use the result to decide which
	/// repository a directory holds, and then write to it, a confident wrong answer is the worst outcome.
	/// </remarks>
	public static string? ParseRepoIdentity(string remoteUrl)
	{
		const string GitHubHost = "github.com";

		var value = remoteUrl.Trim();
		string path;

		var colon = value.IndexOf(':');
		if (value.Contains("://", StringComparison.Ordinal))
		{
			// https://github.com/owner/name.git, and the ssh:// form of the same.
			var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
			var pathStart = value.IndexOf('/', authorityStart);
			if (pathStart < 0)
			{
				return null;
			}

			// Ignore any user@ prefix, and any :port suffix, before comparing the host.
			var authority = value[authorityStart..pathStart];
			var at = authority.LastIndexOf('@');
			var host = at >= 0 ? authority[(at + 1)..] : authority;
			var port = host.IndexOf(':');
			if (port >= 0)
			{
				host = host[..port];
			}

			if (!string.Equals(host, GitHubHost, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			path = value[pathStart..];
		}
		else if (colon > 0)
		{
			// git@github.com:owner/name.git — everything before the first colon is user@host.
			var authority = value[..colon];
			var at = authority.LastIndexOf('@');
			var host = at >= 0 ? authority[(at + 1)..] : authority;

			if (!string.Equals(host, GitHubHost, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			path = value[(colon + 1)..];
		}
		else
		{
			return null;
		}

		if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			path = path[..^4];
		}

		// Exactly owner/name: a longer path is not a GitHub repository URL, and silently taking its last
		// two segments is how an Azure DevOps project path turns into a convincing identity.
		var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		return segments.Length == 2 ? $"{segments[0]}/{segments[1]}" : null;
	}

	/// <summary>
	/// Gets the local path for a repository, given its <c>owner/name</c> identity.
	/// </summary>
	/// <remarks>
	/// Paths are qualified by owner, so two organisations owning a same-named repository resolve to
	/// different directories instead of colliding in one. A bare name (no owner) is still accepted and
	/// resolves directly under the root, so a hand-configured or legacy layout keeps working.
	/// </remarks>
	public string GetLocalPath(string repoIdentity)
	{
		var (owner, name) = SplitIdentity(repoIdentity);
		return owner is null
			? Path.Combine(GetReposRoot(), name)
			: Path.Combine(GetReposRoot(), owner, name);
	}

	/// <summary>
	/// Splits an <c>owner/name</c> identity into its parts, tolerating a bare name.
	/// </summary>
	private static (string? Owner, string Name) SplitIdentity(string repoIdentity)
	{
		var segments = repoIdentity.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return segments.Length >= 2
			? (segments[^2], segments[^1])
			: (null, segments.Length == 1 ? segments[0] : repoIdentity);
	}

	/// <summary>
	/// Gets the URL of the clone's <c>origin</c> remote, or null if it cannot be determined.
	/// </summary>
	/// <remarks>
	/// A directory's name does not prove which repository is checked out in it — it can be renamed, or
	/// re-pointed at a fork. This is the only reliable way to find out, and callers use it to confirm
	/// they are about to write to the repository they think they are.
	/// </remarks>
	public async Task<string?> GetOriginUrlAsync(string repoIdentity, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", "remote get-url origin", cancellationToken)
			.ConfigureAwait(false);

		return exitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
	}

	/// <summary>
	/// Counts the commits on <c>origin</c> that this checkout does not have, fetching first. Returns null
	/// if that cannot be established.
	/// </summary>
	/// <remarks>
	/// Deliberately only "behind", not <see cref="IsSyncedWithOriginAsync"/>'s "behind or ahead": having
	/// local commits to push is the normal state at the point this matters, whereas missing commits means
	/// whatever is about to be committed was decided against a repository that has since moved on.
	/// </remarks>
	public async Task<int?> CountCommitsBehindOriginAsync(string repoIdentity, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (fetchExit, _) = await RunCommandAsync(path, "git", "fetch --prune", cancellationToken).ConfigureAwait(false);
		if (fetchExit != 0)
		{
			return null;
		}

		var (branchExit, branchOutput) = await RunCommandAsync(path, "git", "rev-parse --abbrev-ref HEAD", cancellationToken).ConfigureAwait(false);
		if (branchExit != 0)
		{
			return null;
		}

		var branch = branchOutput.Trim();
		var (behindExit, behindOutput) = await RunCommandAsync(
			path, "git", $"rev-list --count HEAD..origin/{branch}", cancellationToken).ConfigureAwait(false);

		// A missing remote-tracking branch fails here, and is indistinguishable from a real error.
		return behindExit == 0 && int.TryParse(behindOutput.Trim(), out var behind) ? behind : null;
	}

	/// <summary>
	/// Throws away everything uncommitted in a clone: modifications to tracked files, and untracked files
	/// and directories. Returns the <c>git status</c> lines that were discarded.
	/// </summary>
	/// <remarks>
	/// Both halves are needed, because a remediation does not only edit files — it adds them (a
	/// <c>xunit.runner.json</c>, a <c>global.json</c>), and <c>reset --hard</c> alone would leave those
	/// behind to be swept into the next commit. Safe here only because this runs in a clone of the app's
	/// own making, which is the reason those live somewhere separate from the user's checkouts.
	/// </remarks>
	public async Task<(bool Success, IReadOnlyList<string> Discarded)> DiscardLocalChangesAsync(
		string repoIdentity,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return (false, []);
		}

		// Captured first: afterwards there is nothing left to report.
		var discarded = await GetWorkingTreeStatusPreviewAsync(repoIdentity, int.MaxValue, cancellationToken)
			.ConfigureAwait(false);

		var (resetExit, _) = await RunCommandAsync(path, "git", "reset --hard HEAD", cancellationToken).ConfigureAwait(false);
		var (cleanExit, _) = await RunCommandAsync(path, "git", "clean -fd", cancellationToken).ConfigureAwait(false);

		_logger.LogInformation("Discarded {Count} local change(s) in {Path}", discarded.Count, path);
		return (resetExit == 0 && cleanExit == 0, discarded);
	}

	/// <summary>
	/// Opens a folder in this machine's file manager. Returns false, with the reason, if it cannot be.
	/// </summary>
	/// <remarks>
	/// Runs on the server, which is the user's own machine while the dashboard is run locally — the same
	/// assumption "Open in IDE" already makes. <c>UseShellExecute</c> hands the path to the shell rather
	/// than naming a file manager, so there is nothing platform-specific to get wrong.
	/// </remarks>
	public (bool Success, string Message) OpenFolder(string path)
	{
		if (!Directory.Exists(path))
		{
			return (false, $"{path} does not exist yet.");
		}

		try
		{
			using var process = Process.Start(new ProcessStartInfo
			{
				FileName = path,
				UseShellExecute = true
			});

			_logger.LogInformation("Opened folder {Path} in the file manager", path);
			return (true, path);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not open folder {Path}", path);
			return (false, ex.Message);
		}
	}

	/// <summary>
	/// Checks if a repository is cloned locally.
	/// </summary>
	public bool IsClonedLocally(string repoIdentity)
	{
		var path = GetLocalPath(repoIdentity);
		return Directory.Exists(Path.Combine(path, ".git"));
	}

	/// <summary>
	/// Finds the first .slnx file in a local repository, or null if none exists.
	/// </summary>
	public string? FindSlnxFile(string repoIdentity)
	{
		var path = GetLocalPath(repoIdentity);
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
	public async Task<string?> GetCurrentBranchAsync(string repoIdentity, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", "rev-parse --abbrev-ref HEAD", cancellationToken).ConfigureAwait(false);
		return exitCode == 0 ? output.Trim() : null;
	}

	/// <summary>
	/// Gets the remote default branch name for origin when available.
	/// </summary>
	public async Task<string?> GetRemoteDefaultBranchAsync(string repoIdentity, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		_ = await RunCommandAsync(path, "git", "fetch --prune origin", cancellationToken).ConfigureAwait(false);
		_ = await RunCommandAsync(path, "git", "remote set-head origin -a", cancellationToken).ConfigureAwait(false);

		var (showExitCode, showOutput) = await RunCommandAsync(path, "git", "remote show origin", cancellationToken).ConfigureAwait(false);
		if (showExitCode == 0)
		{
			var headBranchLine = showOutput
				.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
				.FirstOrDefault(line => line.Contains("HEAD branch:", StringComparison.OrdinalIgnoreCase));

			if (headBranchLine is not null)
			{
				var idx = headBranchLine.IndexOf(':');
				if (idx >= 0)
				{
					var remoteHeadBranch = headBranchLine[(idx + 1)..].Trim();
					if (!string.IsNullOrWhiteSpace(remoteHeadBranch) &&
						!string.Equals(remoteHeadBranch, "(unknown)", StringComparison.OrdinalIgnoreCase))
					{
						return remoteHeadBranch;
					}
				}
			}
		}

		var (headExitCode, headOutput) = await RunCommandAsync(path, "git", "symbolic-ref --quiet --short refs/remotes/origin/HEAD", cancellationToken).ConfigureAwait(false);
		if (headExitCode == 0)
		{
			var full = headOutput.Trim();
			if (!string.IsNullOrWhiteSpace(full))
			{
				return full.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
					? full["origin/".Length..]
					: full;
			}
		}

		return null;
	}

	/// <summary>
	/// Checks whether the working tree is clean.
	/// </summary>
	public async Task<bool?> IsWorkingTreeCleanAsync(string repoIdentity, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", "status --porcelain", cancellationToken).ConfigureAwait(false);
		if (exitCode != 0)
		{
			_logger.LogWarning("Failed to determine working tree cleanliness for {RepoName}. git status exit code: {ExitCode}", repoIdentity, exitCode);
			return null;
		}

		return string.IsNullOrWhiteSpace(output);
	}

	/// <summary>
	/// Gets a preview of git status porcelain lines for dirty working tree diagnostics.
	/// </summary>
	public async Task<IReadOnlyList<string>> GetWorkingTreeStatusPreviewAsync(
		string repoIdentity,
		int maxLines = 3,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path) || maxLines <= 0)
		{
			return [];
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", "status --porcelain", cancellationToken).ConfigureAwait(false);
		if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
		{
			return [];
		}

		return output
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
			.Take(maxLines)
			.ToArray();
	}

	/// <summary>
	/// Returns the latest git tag reachable from HEAD (e.g. "1.0.55").
	/// Returns null if the repo has no tags or the command fails.
	/// </summary>
	public async Task<string?> GetLatestTagAsync(string repoIdentity, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		if (!Directory.Exists(path))
		{
			return null;
		}

		var (exitCode, output) = await RunCommandAsync(path, "git", "describe --tags --abbrev=0", cancellationToken).ConfigureAwait(false);
		if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
		{
			return null;
		}

		return output.Trim();
	}

	/// <summary>
	/// Checks whether the local branch is in sync with its origin counterpart.
	/// Performs a git fetch first, then compares HEAD against origin/{branch}.
	/// Returns true if HEAD matches origin/{branch} (not behind and not ahead).
	/// </summary>
	public async Task<bool?> IsSyncedWithOriginAsync(string repoIdentity, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
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
	/// Checks whether git can reach GitHub over https, and — when it cannot because the TLS chain is
	/// not trusted — whether switching git to the Windows certificate store would fix it.
	/// </summary>
	/// <remarks>
	/// Worth distinguishing, because a TLS trust failure is not a fault in this app or in the network:
	/// it is git's bundled OpenSSL not knowing the certificate an intercepting proxy presents, while
	/// every browser on the machine trusts it via the Windows store. The remedy is one setting, and the
	/// only honest way to recommend it is to try it first — hence the second probe, which runs only
	/// after the first has failed for that reason.
	/// </remarks>
	public async Task<GitRemoteAccessResult> CheckRemoteAccessAsync(
		string probeUrl,
		CancellationToken cancellationToken = default)
	{
		var workingDirectory = Directory.GetCurrentDirectory();

		var (exitCode, output) = await RunCommandAsync(
			workingDirectory, "git", $"ls-remote --quiet {probeUrl} HEAD", cancellationToken).ConfigureAwait(false);
		if (exitCode == 0)
		{
			return new GitRemoteAccessResult(GitRemoteAccess.Ok, output, SchannelWouldFix: false);
		}

		if (!LooksLikeTlsTrustFailure(output))
		{
			_logger.LogWarning("git cannot reach {Url}: {Output}", probeUrl, output);
			return new GitRemoteAccessResult(GitRemoteAccess.Unreachable, output, SchannelWouldFix: false);
		}

		var (schannelExitCode, _) = await RunCommandAsync(
			workingDirectory,
			"git",
			$"-c http.sslBackend=schannel ls-remote --quiet {probeUrl} HEAD",
			cancellationToken).ConfigureAwait(false);

		_logger.LogWarning(
			"git cannot verify the TLS chain to {Url}; the Windows certificate store {Verdict}.",
			probeUrl,
			schannelExitCode == 0 ? "would fix it" : "would not fix it either");

		return new GitRemoteAccessResult(GitRemoteAccess.TlsTrustFailure, output, schannelExitCode == 0);
	}

	/// <summary>
	/// Whether git's failure output is about not trusting the certificate chain, as opposed to the host
	/// being unreachable or the repository not existing.
	/// </summary>
	private static bool LooksLikeTlsTrustFailure(string output) =>
		output.Contains("unable to get local issuer certificate", StringComparison.OrdinalIgnoreCase)
		|| output.Contains("SSL certificate problem", StringComparison.OrdinalIgnoreCase)
		|| output.Contains("certificate verify failed", StringComparison.OrdinalIgnoreCase)
		|| output.Contains("self-signed certificate", StringComparison.OrdinalIgnoreCase)
		|| output.Contains("self signed certificate", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Clones a repository from GitHub.
	/// </summary>
	public async Task<(bool Success, string Output)> CloneAsync(
		string cloneUrl,
		string repoIdentity,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var targetPath = GetLocalPath(repoIdentity);

		// The owner directory (and the clone root itself, on first use) will not exist yet.
		var parent = Path.GetDirectoryName(targetPath) ?? GetReposRoot();
		Directory.CreateDirectory(parent);

		_logger.LogInformation("Cloning {Url} to {Path}", cloneUrl, targetPath);
		return await RunCommandWithStreamingAsync(parent, "git", $"clone {cloneUrl} {Path.GetFileName(targetPath)}", onOutput, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Creates a branch, commits changes, and pushes.
	/// </summary>
	public async Task<(bool Success, string Output)> CreateBranchCommitPushAsync(
		string repoIdentity,
		string branchName,
		string commitMessage,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);

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
		string repoIdentity,
		string branchName,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);

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
		string repoIdentity,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Building in {Path}", path);
		return await RunCommandWithStreamingAsync(path, "dotnet", "build --no-restore --verbosity normal", onOutput, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs a full dotnet build (with restore) on the repository. Used by the regression guard so a
	/// stale/absent restore never produces a false build failure (and a false rollback).
	/// </summary>
	public async Task<(bool Success, string Output)> BuildWithRestoreAsync(
		string repoIdentity,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Building (with restore) in {Path}", path);
		return await RunCommandWithStreamingAsync(path, "dotnet", "build --verbosity quiet", onOutput, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Gets the most recent commits (hash + subject line), newest first.
	/// </summary>
	public async Task<IReadOnlyList<(string Hash, string Subject)>> GetRecentCommitsAsync(
		string repoIdentity,
		int count = 20,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		var (ok, output) = await RunCommandWithStreamingAsync(
			path, "git", $"log -n {count} --pretty=format:%H%x1f%s", null, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return [];
		}

		var commits = new List<(string Hash, string Subject)>();
		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = line.Split('\u001f', 2);
			if (parts.Length == 2)
			{
				commits.Add((parts[0].Trim(), parts[1].Trim()));
			}
		}

		return commits;
	}

	/// <summary>
	/// Builds the repository at a specific commit (detached HEAD), always restoring the original
	/// branch afterwards. Used to confirm whether a build regression was introduced by our commits.
	/// </summary>
	public async Task<(bool Success, string Output)> BuildAtCommitAsync(
		string repoIdentity,
		string commitHash,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		var branch = await GetCurrentBranchAsync(repoIdentity, cancellationToken).ConfigureAwait(false);

		var (ok, output) = await RunCommandWithStreamingAsync(path, "git", $"checkout {commitHash}", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		try
		{
			// Use a full build (with restore) since dependencies may differ at the parent commit.
			return await RunCommandWithStreamingAsync(path, "dotnet", "build --verbosity quiet", onOutput, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			if (!string.IsNullOrWhiteSpace(branch))
			{
				await RunCommandWithStreamingAsync(path, "git", $"checkout {branch}", onOutput, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// Reverts every commit after <paramref name="lastGoodCommitHash"/> up to HEAD as a single revert
	/// commit, then pushes. Used to automatically undo a regression we introduced. On revert conflict
	/// the revert is aborted and failure is returned (no partial state is pushed).
	/// </summary>
	public async Task<(bool Success, string Output)> RevertRangeAndPushAsync(
		string repoIdentity,
		string lastGoodCommitHash,
		string message,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);

		var (ok, output) = await RunCommandWithStreamingAsync(
			path, "git", $"revert --no-edit --no-commit {lastGoodCommitHash}..HEAD", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			await RunCommandWithStreamingAsync(path, "git", "revert --abort", onOutput, cancellationToken).ConfigureAwait(false);
			return (false, output);
		}

		(ok, output) = await RunCommandWithStreamingAsync(path, "git", $"commit -m \"{message}\"", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		return await RunCommandWithStreamingAsync(path, "git", "push", onOutput, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Syncs a local repository with its remote: fetch, pull (rebase), push.
	/// </summary>
	public async Task<(bool Success, string Output)> GitSyncAsync(
		string repoIdentity,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
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
	/// Commits all local changes, fetches from origin, rebases, and pushes.
	/// </summary>
	public async Task<(bool Success, string Output)> CommitAndPushAsync(
		string repoIdentity,
		string commitMessage,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Commit and push in {Path}", path);

		// Stage all changes
		var (ok, output) = await RunCommandWithStreamingAsync(path, "git", "add -A", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Commit (allow empty commit to succeed gracefully)
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", $"commit -m \"{commitMessage}\"", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			// Check if "nothing to commit" — that's fine, continue with fetch/rebase/push
			if (!output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
			{
				return (false, output);
			}
		}

		// Fetch
		(ok, output) = await RunCommandWithStreamingAsync(path, "git", "fetch --prune", onOutput, cancellationToken).ConfigureAwait(false);
		if (!ok)
		{
			return (false, output);
		}

		// Rebase on top of remote
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
	/// Uses --no-build because the Build step has already compiled the solution.
	/// This avoids the confusing "Build FAILED." MSBuild output that appears
	/// when tests fail (MSBuild's VSTest target reports its failure as a build failure).
	/// </summary>
	public async Task<(bool Success, string Output)> RunTestsAsync(
		string repoIdentity,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		_logger.LogInformation("Running tests in {Path}", path);

		var testProjects = GetConfiguredTestProjects(path);
		if (testProjects.Count == 0)
		{
			return await RunCommandWithStreamingAsync(path, "dotnet", "test --no-build --no-restore --verbosity normal", onOutput, cancellationToken).ConfigureAwait(false);
		}

		var combinedOutput = new List<string>();
		var allPassed = true;

		foreach (var (projectPath, projectConfig) in testProjects)
		{
			if (projectConfig?.DefaultTestingLevel == ProjectTestingLevel.None)
			{
				onOutput?.Invoke($"⏭️ Skipping tests for {projectPath} (DefaultTestingLevel=None).");
				continue;
			}

			var filter = BuildTestFilter(projectConfig);
			var args = $"test \"{projectPath}\" --no-build --no-restore --verbosity normal";
			if (!string.IsNullOrWhiteSpace(filter))
			{
				args += $" --filter \"{filter}\"";
			}

			onOutput?.Invoke($"▶ dotnet {args}");
			var (success, output) = await RunCommandWithStreamingAsync(path, "dotnet", args, onOutput, cancellationToken).ConfigureAwait(false);
			combinedOutput.Add(output);
			allPassed &= success;
		}

		return (allPassed, string.Join('\n', combinedOutput));
	}

	private static List<(string ProjectPath, NugetManagementProjectConfig? Config)> GetConfiguredTestProjects(string localPath)
	{
		var configPath = Path.Combine(localPath, NugetManagementRepositoryConfig.FileName);
		var rawConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
		var repositoryConfig = NugetManagementRepositoryConfigParser.Parse(rawConfig);

		var projects = Directory
			.EnumerateFiles(localPath, "*.csproj", SearchOption.AllDirectories)
			.Select(fullPath => Path.GetRelativePath(localPath, fullPath).Replace('\\', '/'))
			.Where(relativePath => !relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
			.Where(relativePath => !relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
			.ToList();

		var selected = new List<(string ProjectPath, NugetManagementProjectConfig? Config)>();
		foreach (var project in projects)
		{
			var projectConfig = repositoryConfig?.GetProjectConfig(project);
			if (projectConfig?.TestingTreatment == ProjectTreatment.Exclude)
			{
				continue;
			}

			if (projectConfig?.TestingTreatment == ProjectTreatment.Include)
			{
				selected.Add((project, projectConfig));
				continue;
			}

			if (projectConfig?.Treatment == ProjectTreatment.Exclude)
			{
				continue;
			}

			if (IsLikelyTestProject(project) && IsAutoIncludedProject(project))
			{
				selected.Add((project, projectConfig));
			}
		}

		return selected;
	}

	private static string BuildTestFilter(NugetManagementProjectConfig? config)
	{
		if (config is null)
		{
			return string.Empty;
		}

		var baseClauses = new List<string>();

		if (config.DefaultTestingLevel == ProjectTestingLevel.Smoke)
		{
			baseClauses.Add("(TestCategory!=Integration&Category!=Integration)");
			baseClauses.Add("(TestCategory!=Slow&Category!=Slow)");
		}

		var includeCollections = config.CollectionTreatments
			.Where(rule => !string.IsNullOrWhiteSpace(rule.Name) && rule.Treatment == ProjectTreatment.Include)
			.Select(rule => rule.Name.Trim())
			.ToArray();

		var excludeCollections = config.CollectionTreatments
			.Where(rule => !string.IsNullOrWhiteSpace(rule.Name) && rule.Treatment == ProjectTreatment.Exclude)
			.Select(rule => rule.Name.Trim())
			.ToArray();

		if (includeCollections.Length > 0)
		{
			var includeExpression = includeCollections
				.Select(value => $"(TestCategory={value}|Category={value})")
				.ToArray();

			if (includeExpression.Length > 0)
			{
				baseClauses.Add("(" + string.Join("|", includeExpression) + ")");
			}
		}

		if (excludeCollections.Length > 0)
		{
			foreach (var value in excludeCollections)
			{
				baseClauses.Add($"(TestCategory!={value}&Category!={value})");
			}
		}

		var includeTests = config.TestTreatments
			.Where(rule => !string.IsNullOrWhiteSpace(rule.Id) && rule.Treatment == ProjectTreatment.Include)
			.Select(rule => $"FullyQualifiedName~{rule.Id.Trim()}")
			.ToArray();

		var excludeTests = config.TestTreatments
			.Where(rule => !string.IsNullOrWhiteSpace(rule.Id) && rule.Treatment == ProjectTreatment.Exclude)
			.Select(rule => $"FullyQualifiedName!~{rule.Id.Trim()}")
			.ToArray();

		var baseExpression = string.Join('&', baseClauses);
		var explicitTestIncludeExpression = includeTests.Length == 0
			? string.Empty
			: "(" + string.Join('|', includeTests) + ")";

		var combined = baseExpression;
		if (!string.IsNullOrWhiteSpace(explicitTestIncludeExpression))
		{
			combined = string.IsNullOrWhiteSpace(baseExpression)
				? explicitTestIncludeExpression
				: $"(({baseExpression})|{explicitTestIncludeExpression})";
		}

		foreach (var exclusion in excludeTests)
		{
			combined = string.IsNullOrWhiteSpace(combined)
				? exclusion
				: $"({combined})&({exclusion})";
		}

		return combined;
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

	/// <summary>
	/// Runs the Publish.ps1 script.
	/// </summary>
	public async Task<(bool Success, string Output)> RunPublishScriptAsync(
		string repoIdentity,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
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
				CreateNoWindow = true,
				Environment = { ["NO_COLOR"] = "1" }
			}
		};

		process.Start();
		try
		{
			var output = StripAnsi(await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
			var error = StripAnsi(await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

			return (process.ExitCode, string.IsNullOrEmpty(error) ? output : $"{output}\n{error}");
		}
		catch (OperationCanceledException)
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}

			throw;
		}
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
				CreateNoWindow = true,
				Environment = { ["NO_COLOR"] = "1" }
			}
		};

		var outputLines = new List<string>();

		process.OutputDataReceived += (_, e) =>
		{
			if (e.Data is not null)
			{
				var line = StripAnsi(e.Data);
				outputLines.Add(line);
				onOutput?.Invoke(line);
			}
		};

		process.ErrorDataReceived += (_, e) =>
		{
			if (e.Data is not null)
			{
				var line = StripAnsi(e.Data);
				outputLines.Add(line);
				onOutput?.Invoke(line);
			}
		};

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		try
		{
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}

			throw;
		}

		var fullOutput = string.Join('\n', outputLines);
		return (process.ExitCode == 0, fullOutput);
	}
}
