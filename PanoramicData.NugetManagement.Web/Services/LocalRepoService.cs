using System.Diagnostics;
using System.Text.RegularExpressions;
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

	private readonly RuntimeSettingsService _runtimeSettings;
	private readonly ILogger<LocalRepoService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="LocalRepoService"/> class.
	/// </summary>
	/// <remarks>
	/// Takes <see cref="RuntimeSettingsService"/> rather than <see cref="AppSettings"/> so the clone root
	/// is read from the one place that knows the answer. Reading AppSettings directly worked only because
	/// the settings service copies any persisted override into it on construction — which made the root
	/// depend on whether that had happened yet, and it had not when the root was first needed at start-up.
	/// </remarks>
	public LocalRepoService(RuntimeSettingsService runtimeSettings, ILogger<LocalRepoService> logger)
	{
		_runtimeSettings = runtimeSettings;
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
	/// inside whatever the user had checked out there. Since <see cref="CommitToPullRequestBranchAsync"/> stages with
	/// <c>git add -A</c>, a bulk fix could commit and push the user's uncommitted work along with its own
	/// changes, on whatever branch they happened to be on. Cloning into the app's own root keeps the two
	/// apart, and lets clones be qualified by owner (see <see cref="GetLocalPath"/>) without dictating how
	/// the user arranges their own repositories.
	/// </remarks>
	public string GetReposRoot() => _runtimeSettings.LocalReposRoot ?? GetDefaultReposRoot();

	/// <summary>
	/// Creates the clone root if it is not there, and returns it.
	/// </summary>
	/// <remarks>
	/// Called at start-up and whenever the root changes, so a configured root that does not exist is not a
	/// state the app can be in. It used to be: the root was only created as a side effect of the first
	/// clone, so until then — or if the folder was deleted afterwards — the settings page named a location
	/// that was not there and offered a dead Open button for it. The folder belongs to the app, so its
	/// existence is not something to wait for permission on.
	/// </remarks>
	public string EnsureReposRootExists()
	{
		var root = GetReposRoot();

		try
		{
			Directory.CreateDirectory(root);
		}
		catch (Exception ex)
		{
			// A root that cannot be created is worth a line in the log, but not worth refusing to start:
			// everything that writes to it reports its own failure, and the path is on the settings page.
			_logger.LogWarning(ex, "Could not create the clone root {Root}", root);
		}

		return root;
	}

	/// <summary>
	/// The clone root used when nothing has been configured, which is also what the settings page names so
	/// that choosing it again is how you go back to it.
	/// </summary>
	public static string GetDefaultReposRoot()
	{
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
		CancellationToken cancellationToken)
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

			var (exitCode, output) = await RunCommandAsync(path, "git", ["remote", "get-url", "origin"], cancellationToken)
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
		var value = remoteUrl.Trim();
		var colon = value.IndexOf(':');

		var path = value.Contains("://", StringComparison.Ordinal)
			? PathFromUrlRemote(value)
			: colon > 0 ? PathFromScpRemote(value, colon) : null;
		if (path is null)
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

	private static string? PathFromUrlRemote(string value)
	{
		// https://github.com/owner/name.git, and the ssh:// form of the same.
		var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
		var pathStart = value.IndexOf('/', authorityStart);
		if (pathStart < 0)
		{
			return null;
		}

		// Ignore any user@ prefix, and any :port suffix, before comparing the host.
		var host = HostOf(value[authorityStart..pathStart]);
		var port = host.IndexOf(':');
		if (port >= 0)
		{
			host = host[..port];
		}

		return IsGitHubHost(host) ? value[pathStart..] : null;
	}

	private static string? PathFromScpRemote(string value, int colon)
		// git@github.com:owner/name.git — everything before the first colon is user@host.
		=> IsGitHubHost(HostOf(value[..colon])) ? value[(colon + 1)..] : null;

	private static string HostOf(string authority)
	{
		var at = authority.LastIndexOf('@');
		return at >= 0 ? authority[(at + 1)..] : authority;
	}

	private static bool IsGitHubHost(string host)
		=> string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase);

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
	/// Runs the Publish.ps1 script.
	/// </summary>
	public async Task<(bool Success, string Output)> RunPublishScriptAsync(
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var path = GetLocalPath(repoIdentity);
		var publishScript = Path.Combine(path, "Publish.ps1");
		if (!File.Exists(publishScript))
		{
			return (false, "Publish.ps1 not found.");
		}

		_logger.LogInformation("Running Publish.ps1 in {Path}", path);
		return await RunCommandWithStreamingAsync(path, "pwsh", ["-ExecutionPolicy", "Bypass", "-File", "Publish.ps1"], onOutput, cancellationToken, null).ConfigureAwait(false);
	}

	/// <summary>
	/// Builds the <see cref="ProcessStartInfo"/> shared by <see cref="RunCommandAsync"/> and
	/// <see cref="RunCommandWithStreamingAsync"/>.
	/// </summary>
	/// <remarks>
	/// Arguments travel as discrete <see cref="ProcessStartInfo.ArgumentList"/> entries rather than a
	/// pre-formatted <see cref="ProcessStartInfo.Arguments"/> string. A value built by string
	/// concatenation (a branch name, commit message, or URL) can contain a space, a quote, or a leading
	/// "-" that would otherwise be re-split into extra arguments or mistaken for a flag by the child
	/// process; ArgumentList passes each token through untouched (Codacy SEC-01: CommandInjection).
	/// </remarks>
	private static readonly string[] _inheritedSdkVariables =
	[
		"MSBUILD_EXE_PATH",
		"MSBuildExtensionsPath",
		"MSBuildSDKsPath",
		"DOTNET_HOST_PATH",
		"DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
		"DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR",
		"DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER"
	];

	internal static ProcessStartInfo BuildProcessStartInfo(
		string workingDirectory,
		string fileName,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string?>? environment)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = fileName, // nosemgrep
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			Environment = { ["NO_COLOR"] = "1" }
		};

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		// Reused MSBuild nodes outlive each build for minutes, pile up by the hundreds across concurrent clones, and hand .NET 11's out-of-process task host to the wrong build (MSB4216).
		if (string.Equals(Path.GetFileNameWithoutExtension(fileName), "dotnet", StringComparison.OrdinalIgnoreCase))
		{
			startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
			startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

			// `dotnet run` exports its own SDK's MSBuild paths. A repo whose global.json selects a different SDK
			// then runs a mix of two, and fails with MSB4216/MSB4242 on a repo that builds fine from a shell.
			foreach (var inherited in _inheritedSdkVariables)
			{
				startInfo.Environment.Remove(inherited);
			}
		}

		// A null value removes the variable rather than blanking it: a credential set to the empty string
		// is still a variable a test can find and choose to use.
		foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
		{
			if (value is null)
			{
				startInfo.Environment.Remove(name);
			}
			else
			{
				startInfo.Environment[name] = value;
			}
		}

		return startInfo;
	}

	/// <summary>
	/// Runs a command and returns exit code + output.
	/// </summary>
	public static async Task<(int ExitCode, string Output)> RunCommandAsync(
		string workingDirectory,
		string fileName,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		using var process = new Process
		{
			StartInfo = BuildProcessStartInfo(workingDirectory, fileName, arguments, null)
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
		IReadOnlyList<string> arguments,
		Action<string>? onOutput,
		CancellationToken cancellationToken,
		IReadOnlyDictionary<string, string?>? environment)
	{
		using var process = new Process
		{
			StartInfo = BuildProcessStartInfo(workingDirectory, fileName, arguments, environment)
		};

		var outputLines = new List<string>();

		void Capture(object? sender, DataReceivedEventArgs e)
		{
			if (e.Data is null)
			{
				return;
			}

			var line = StripAnsi(e.Data);
			outputLines.Add(line);
			onOutput?.Invoke(line);
		}

		process.OutputDataReceived += Capture;
		process.ErrorDataReceived += Capture;

		process.Start();
		using var job = ProcessJob.TryAssign(process);
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		await WaitForExitAsync(process, job, cancellationToken).ConfigureAwait(false);

		var fullOutput = string.Join('\n', outputLines);
		return (process.ExitCode == 0, fullOutput);
	}

	private static async Task WaitForExitAsync(Process process, IDisposable? job, CancellationToken cancellationToken)
	{
		try
		{
			// A bounded WaitForExit does not wait for the output pipes. WaitForExitAsync does, and a worker the
			// command left running holds those pipes open, so the call would not return until it died.
			await Task.Run(
				() =>
				{
					while (!process.WaitForExit(250))
					{
						cancellationToken.ThrowIfCancellationRequested();
					}
				},
				cancellationToken).ConfigureAwait(false);

			job?.Dispose();

			using var flush = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			flush.CancelAfter(TimeSpan.FromSeconds(30));
			try
			{
				await process.WaitForExitAsync(flush.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				// Something outside the job still holds the pipes; what was read so far is the output.
			}
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
}
