using System.Diagnostics;
using System.Runtime.InteropServices;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Detects installed IDEs on the local machine and provides default selection logic.
/// </summary>
public class IdeDetectionService
{
	private const int SW_SHOWMAXIMIZED = 3;

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

	private readonly ILogger<IdeDetectionService> _logger;
	private List<InstalledIde>? _detectedIdes;

	/// <summary>
	/// Initializes a new instance of the <see cref="IdeDetectionService"/> class.
	/// </summary>
	/// <param name="logger">Logger used for IDE launch diagnostics.</param>
	public IdeDetectionService(ILogger<IdeDetectionService> logger)
	{
		_logger = logger;
	}

	/// <summary>
	/// Gets the list of detected IDE installations on the local machine.
	/// </summary>
	public IReadOnlyList<InstalledIde> DetectedIdes => _detectedIdes ??= Detect();

	/// <summary>
	/// Returns the IDE id that should be used as the default when the user has not yet chosen.
	/// Priority order: VS Code Insiders, VS 2026 Insiders, VS Code, VS 2026 (any edition), Rider.
	/// Returns null if no IDE is detected.
	/// </summary>
	public string? GetDefaultIdeId()
	{
		var ides = DetectedIdes;
		if (ides.Count == 0)
		{
			return null;
		}

		string[] priorityOrder =
		[
			"vscode-insiders",
			"vs2026-insiders",
			"vscode",
			"vs2026-enterprise",
			"vs2026-professional",
			"vs2026-community",
			"rider"
		];

		foreach (var id in priorityOrder)
		{
			if (ides.Any(i => i.Id == id))
			{
				return id;
			}
		}

		// Fallback to first detected
		return ides[0].Id;
	}

	/// <summary>
	/// Clears the cached IDE list so the next access to <see cref="DetectedIdes"/> re-detects.
	/// </summary>
	public void Refresh() => _detectedIdes = null;

	/// <summary>
	/// Opens the specified local path in the given IDE.
	/// </summary>
	/// <param name="ide">The IDE to open.</param>
	/// <param name="localPath">The local repository path to open.</param>
	internal IdeLaunchResult OpenInIde(InstalledIde ide, string localPath)
	{
		// Find a .slnx or .sln file in the local path
		string? targetPath = null;

		if (ide.OpensSolutionFiles)
		{
			targetPath = Directory.EnumerateFiles(localPath, "*.slnx").FirstOrDefault()
				?? Directory.EnumerateFiles(localPath, "*.sln").FirstOrDefault();
		}

		targetPath ??= localPath;

		_logger.LogInformation("OpenInIde requested: IdeId={IdeId}, DisplayName={DisplayName}, ExecutablePath={ExecutablePath}, LocalPath={LocalPath}, TargetPath={TargetPath}",
			ide.Id,
			ide.DisplayName,
			ide.ExecutablePath,
			localPath,
			targetPath);

		var psi = new ProcessStartInfo
		{
			FileName = ide.ExecutablePath,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		psi.ArgumentList.Add(targetPath);

		Process? process;
		try
		{
			process = Process.Start(psi);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "OpenInIde failed to start process for IdeId={IdeId} at {ExecutablePath}", ide.Id, ide.ExecutablePath);
			return IdeLaunchResult.Failed(
				ide.Id,
				ide.DisplayName,
				ide.ExecutablePath,
				targetPath,
				$"Failed to start process: {ex.Message}");
		}

		if (process is null)
		{
			_logger.LogWarning("OpenInIde Process.Start returned null for IdeId={IdeId} at {ExecutablePath}", ide.Id, ide.ExecutablePath);
			return IdeLaunchResult.Failed(
				ide.Id,
				ide.DisplayName,
				ide.ExecutablePath,
				targetPath,
				"Process.Start returned null.");
		}

		_logger.LogInformation("OpenInIde process launched: IdeId={IdeId}, PID={Pid}", ide.Id, process.Id);

		var result = IdeLaunchResult.Launched(
			ide.Id,
			ide.DisplayName,
			ide.ExecutablePath,
			targetPath,
			process.Id);

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			try
			{
				// Wait briefly for the main window to appear, then bring it to foreground.
				var sw = Stopwatch.StartNew();
				nint mainWindowHandle = nint.Zero;

				while (sw.ElapsedMilliseconds < 5_000)
				{
					process.Refresh();
					if (process.HasExited)
					{
						process.WaitForExit(250);
						var exitCode = process.ExitCode;
						var errorOutput = ReadProcessErrorOutput(process);
						var diagnosticMessage = $"Process exited before creating a visible main window. Exit code: {exitCode}."
							+ (string.IsNullOrWhiteSpace(errorOutput)
								? " No error output was captured."
								: $" Error: {errorOutput}");

						_logger.LogWarning("OpenInIde launched process exited quickly: IdeId={IdeId}, PID={Pid}, ElapsedMs={ElapsedMs}", ide.Id, process.Id, sw.ElapsedMilliseconds);
						result = result with
						{
							ExitedQuickly = true,
							ExitCode = exitCode,
							ErrorOutput = errorOutput,
							DiagnosticMessage = diagnosticMessage
						};
						break;
					}

					mainWindowHandle = process.MainWindowHandle;
					if (mainWindowHandle != nint.Zero)
					{
						break;
					}

					Thread.Sleep(100);
				}

				if (mainWindowHandle != nint.Zero)
				{
					var showResult = ShowWindow(mainWindowHandle, SW_SHOWMAXIMIZED);
					var setForegroundResult = SetForegroundWindow(mainWindowHandle);

					Thread.Sleep(150);

					var foregroundWindow = GetForegroundWindow();
					uint foregroundPid = 0;
					if (foregroundWindow != nint.Zero)
					{
						_ = GetWindowThreadProcessId(foregroundWindow, out foregroundPid);
					}

					var isForeground = foregroundPid == process.Id;
					result = result with
					{
						MainWindowHandle = mainWindowHandle,
						ShowWindowSucceeded = showResult,
						SetForegroundSucceeded = setForegroundResult,
						ForegroundProcessId = foregroundPid,
						IsLaunchedProcessForeground = isForeground,
						DiagnosticMessage = isForeground
							? "Launched IDE window is in the foreground."
							: "Launched IDE window is not the foreground window."
					};

					_logger.LogInformation(
						"OpenInIde foreground check: IdeId={IdeId}, PID={Pid}, MainWindowHandle={MainWindowHandle}, ShowWindow={ShowWindow}, SetForeground={SetForeground}, ForegroundPid={ForegroundPid}, IsForeground={IsForeground}",
						ide.Id,
						process.Id,
						mainWindowHandle,
						showResult,
						setForegroundResult,
						foregroundPid,
						isForeground);
				}
				else if (!result.ExitedQuickly)
				{
					result = result with { DiagnosticMessage = "Could not find a main window handle for the launched process within timeout." };
					_logger.LogWarning("OpenInIde could not resolve main window handle: IdeId={IdeId}, PID={Pid}", ide.Id, process.Id);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "OpenInIde foreground handling failed: IdeId={IdeId}, PID={Pid}", ide.Id, process.Id);
				result = result with { DiagnosticMessage = $"Foreground check failed: {ex.Message}" };
			}
		}

		return result;
	}

	internal sealed record IdeLaunchResult(
		bool Started,
		string IdeId,
		string IdeDisplayName,
		string ExecutablePath,
		string TargetPath,
		int? ProcessId,
		bool ExitedQuickly,
		int? ExitCode,
		string? ErrorOutput,
		nint MainWindowHandle,
		bool? ShowWindowSucceeded,
		bool? SetForegroundSucceeded,
		uint? ForegroundProcessId,
		bool? IsLaunchedProcessForeground,
		string? DiagnosticMessage)
	{
		public static IdeLaunchResult Failed(string ideId, string ideDisplayName, string executablePath, string targetPath, string message)
			=> new(
				Started: false,
				IdeId: ideId,
				IdeDisplayName: ideDisplayName,
				ExecutablePath: executablePath,
				TargetPath: targetPath,
				ProcessId: null,
				ExitedQuickly: false,
				ExitCode: null,
				ErrorOutput: null,
				MainWindowHandle: nint.Zero,
				ShowWindowSucceeded: null,
				SetForegroundSucceeded: null,
				ForegroundProcessId: null,
				IsLaunchedProcessForeground: null,
				DiagnosticMessage: message);

		public static IdeLaunchResult Launched(string ideId, string ideDisplayName, string executablePath, string targetPath, int processId)
			=> new(
				Started: true,
				IdeId: ideId,
				IdeDisplayName: ideDisplayName,
				ExecutablePath: executablePath,
				TargetPath: targetPath,
				ProcessId: processId,
				ExitedQuickly: false,
				ExitCode: null,
				ErrorOutput: null,
				MainWindowHandle: nint.Zero,
				ShowWindowSucceeded: null,
				SetForegroundSucceeded: null,
				ForegroundProcessId: null,
				IsLaunchedProcessForeground: null,
				DiagnosticMessage: null);
	}

	private static string? ReadProcessErrorOutput(Process process)
	{
		try
		{
			var error = process.StandardError.ReadToEnd().Trim();
			if (!string.IsNullOrWhiteSpace(error))
			{
				return error;
			}

			var output = process.StandardOutput.ReadToEnd().Trim();
			return string.IsNullOrWhiteSpace(output) ? null : output;
		}
		catch
		{
			return null;
		}
	}

	private static List<InstalledIde> Detect()
	{
		var ides = new List<InstalledIde>();

		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return ides;
		}

		DetectVsCodeInsiders(ides);
		DetectVsCode(ides);
		DetectVisualStudio2026(ides);
		DetectRider(ides);

		return ides;
	}

	private static void DetectVisualStudio2026(List<InstalledIde> ides)
	{
		var basePath = @"C:\Program Files\Microsoft Visual Studio\18";

		(string folder, string id, string displayName)[] editions =
		[
			("Enterprise", "vs2026-enterprise", "Visual Studio 2026 Enterprise"),
			("Professional", "vs2026-professional", "Visual Studio 2026 Professional"),
			("Community", "vs2026-community", "Visual Studio 2026 Community"),
			("Insiders", "vs2026-insiders", "Visual Studio 2026 Insiders"),
		];

		foreach (var (folder, id, displayName) in editions)
		{
			var exePath = Path.Combine(basePath, folder, "Common7", "IDE", "devenv.exe");
			if (File.Exists(exePath))
			{
				ides.Add(new InstalledIde
				{
					Id = id,
					DisplayName = displayName,
					ExecutablePath = exePath,
					IconCss = "fa-brands fa-microsoft",
					OpensSolutionFiles = true
				});
			}
		}
	}

	private static void DetectVsCode(List<InstalledIde> ides)
	{
		var candidates = new List<string>();

		// User install
		var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		candidates.Add(Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"));

		// System install
		candidates.Add(@"C:\Program Files\Microsoft VS Code\Code.exe");

		foreach (var path in candidates)
		{
			if (File.Exists(path))
			{
				ides.Add(new InstalledIde
				{
					Id = "vscode",
					DisplayName = "Visual Studio Code",
					ExecutablePath = path,
					IconCss = "fa-solid fa-code",
					OpensSolutionFiles = false
				});
				return; // Only add once
			}
		}
	}

	private static void DetectVsCodeInsiders(List<InstalledIde> ides)
	{
		var candidates = new List<string>();

		// User install
		var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		candidates.Add(Path.Combine(localAppData, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe"));

		// System install
		candidates.Add(@"C:\Program Files\Microsoft VS Code Insiders\Code - Insiders.exe");

		foreach (var path in candidates)
		{
			if (File.Exists(path))
			{
				ides.Add(new InstalledIde
				{
					Id = "vscode-insiders",
					DisplayName = "VS Code Insiders",
					ExecutablePath = path,
					IconCss = "fa-solid fa-code",
					OpensSolutionFiles = false
				});
				return; // Only add once
			}
		}
	}

	private static void DetectRider(List<InstalledIde> ides)
	{
		// JetBrains Toolbox install
		var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var toolboxApps = Path.Combine(localAppData, "JetBrains", "Toolbox", "apps", "rider");
		if (Directory.Exists(toolboxApps))
		{
			var riderExe = Directory.EnumerateFiles(toolboxApps, "rider64.exe", SearchOption.AllDirectories)
				.FirstOrDefault();
			if (riderExe is not null)
			{
				ides.Add(new InstalledIde
				{
					Id = "rider",
					DisplayName = "JetBrains Rider",
					ExecutablePath = riderExe,
					IconCss = "fa-solid fa-horse",
					OpensSolutionFiles = true
				});
				return;
			}
		}

		// Standalone install
		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		var jetBrainsDir = Path.Combine(programFiles, "JetBrains");
		if (Directory.Exists(jetBrainsDir))
		{
			var riderDirs = Directory.EnumerateDirectories(jetBrainsDir, "JetBrains Rider*");
			foreach (var dir in riderDirs)
			{
				var exePath = Path.Combine(dir, "bin", "rider64.exe");
				if (File.Exists(exePath))
				{
					ides.Add(new InstalledIde
					{
						Id = "rider",
						DisplayName = "JetBrains Rider",
						ExecutablePath = exePath,
						IconCss = "fa-solid fa-horse",
						OpensSolutionFiles = true
					});
					return;
				}
			}
		}
	}
}
