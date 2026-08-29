using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Starts long-lived child processes - IDEs - so that they outlive this application.
/// </summary>
/// <remarks>
/// On Windows, a process started by a debugger, by <c>dotnet run</c>/<c>dotnet watch</c>, or by many
/// terminal hosts is placed in a job object created with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
/// Children inherit that job, so when this app stops - cleanly or by crashing - Windows kills every IDE
/// it spawned, taking unsaved editor state and in-flight AI sessions with them. Creating the child with
/// <c>CREATE_BREAKAWAY_FROM_JOB</c> puts it outside the job, so it survives. <c>DETACHED_PROCESS</c> and
/// <c>CREATE_NEW_PROCESS_GROUP</c> additionally stop a Ctrl+C in this app's console from reaching it.
/// </remarks>
public static class DetachedProcessLauncher
{
	private const uint CreateBreakawayFromJob = 0x0100_0000;
	private const uint CreateNewProcessGroup = 0x0000_0200;
	private const uint DetachedProcessFlag = 0x0000_0008;
	private const int ErrorAccessDenied = 5;

	/// <summary>
	/// Starts <paramref name="executablePath"/> detached from this process's job object where the platform
	/// supports it, returning null if the process could not be started at all.
	/// </summary>
	/// <param name="executablePath">Full path to the executable to launch.</param>
	/// <param name="argument">Single argument to pass, usually a solution or folder path. May be null.</param>
	/// <param name="workingDirectory">Working directory for the child, or null to inherit this app's.</param>
	/// <param name="logger">Logger used to record how the child was started.</param>
	public static DetachedProcess? Start(
		string executablePath,
		string? argument,
		string? workingDirectory,
		ILogger logger)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			// Unix has no job objects: a child already outlives its parent.
			return StartViaShell(executablePath, argument, workingDirectory);
		}

		var inJob = IsCurrentProcessInJob();
		var commandLine = BuildCommandLine(executablePath, argument);
		var baseFlags = DetachedProcessFlag | CreateNewProcessGroup;

		if (TryCreateProcess(executablePath, commandLine, workingDirectory, baseFlags | CreateBreakawayFromJob, out var process, out var error))
		{
			logger.LogInformation(
				"Started {ExecutablePath} detached from this app's job object (PID={Pid}, InJob={InJob})",
				executablePath,
				process!.Id,
				inJob);
			return process;
		}

		if (error == ErrorAccessDenied)
		{
			// The job forbids breakaway, so the child cannot escape it. Start it anyway - a running IDE that
			// shares this app's lifetime beats no IDE at all - but say plainly what will happen to it.
			logger.LogWarning(
				"This app is in a Windows job object that forbids breakaway, so {ExecutablePath} will be terminated when this app stops. "
				+ "Start the app without a debugger attached to keep spawned IDEs alive.",
				executablePath);

			if (TryCreateProcess(executablePath, commandLine, workingDirectory, baseFlags, out process, out error))
			{
				return process;
			}
		}

		logger.LogWarning(
			"CreateProcess failed for {ExecutablePath} with Win32 error {Error}; falling back to ShellExecute",
			executablePath,
			error);

		return StartViaShell(executablePath, argument, workingDirectory);
	}

	/// <summary>
	/// Builds a Windows command line for <paramref name="executablePath"/> and an optional single argument,
	/// quoting each by the rules <c>CommandLineToArgvW</c> uses to parse them back apart.
	/// </summary>
	/// <param name="executablePath">Full path to the executable.</param>
	/// <param name="argument">Single argument, or null for none.</param>
	public static string BuildCommandLine(string executablePath, string? argument)
	{
		var builder = new StringBuilder();
		AppendQuoted(builder, executablePath);

		if (!string.IsNullOrEmpty(argument))
		{
			builder.Append(' ');
			AppendQuoted(builder, argument);
		}

		return builder.ToString();
	}

	private static void AppendQuoted(StringBuilder builder, string value)
	{
		builder.Append('"');

		var backslashes = 0;
		foreach (var character in value)
		{
			switch (character)
			{
				case '\\':
					backslashes++;
					break;

				case '"':
					// Backslashes before a quote are doubled, then the quote itself is escaped.
					builder.Append('\\', (backslashes * 2) + 1).Append('"');
					backslashes = 0;
					break;

				default:
					builder.Append('\\', backslashes).Append(character);
					backslashes = 0;
					break;
			}
		}

		// A trailing backslash would otherwise escape the closing quote - real for paths like C:\repo\.
		builder.Append('\\', backslashes * 2).Append('"');
	}

	private static DetachedProcess? StartViaShell(string executablePath, string? argument, string? workingDirectory)
	{
		var started = Process.Start(new ProcessStartInfo
		{
			FileName = executablePath,
			Arguments = argument is null ? string.Empty : $"\"{argument}\"",
			WorkingDirectory = workingDirectory ?? string.Empty,
			UseShellExecute = true
		});

		return started is null ? null : DetachedProcess.FromManaged(started);
	}

	private static bool TryCreateProcess(
		string executablePath,
		string commandLine,
		string? workingDirectory,
		uint creationFlags,
		out DetachedProcess? process,
		out int win32Error)
	{
		var startupInfo = new StartupInfo
		{
			cb = Marshal.SizeOf<StartupInfo>()
		};

		// CreateProcessW may write to the command line buffer, so it must be mutable.
		var mutableCommandLine = new StringBuilder(commandLine);

		if (CreateProcessW(
			executablePath,
			mutableCommandLine,
			nint.Zero,
			nint.Zero,
			false,
			creationFlags,
			nint.Zero,
			string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
			ref startupInfo,
			out var information))
		{
			_ = CloseHandle(information.hThread);
			process = new DetachedProcess((int)information.dwProcessId, information.hProcess);
			win32Error = 0;
			return true;
		}

		win32Error = Marshal.GetLastWin32Error();
		process = null;
		return false;
	}

	private static bool IsCurrentProcessInJob()
		=> IsProcessInJob(GetCurrentProcess(), nint.Zero, out var inJob) && inJob;

	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessInformation
	{
		public nint hProcess;
		public nint hThread;
		public uint dwProcessId;
		public uint dwThreadId;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct StartupInfo
	{
		public int cb;
		public nint lpReserved;
		public nint lpDesktop;
		public nint lpTitle;
		public int dwX;
		public int dwY;
		public int dwXSize;
		public int dwYSize;
		public int dwXCountChars;
		public int dwYCountChars;
		public int dwFillAttribute;
		public int dwFlags;
		public short wShowWindow;
		public short cbReserved2;
		public nint lpReserved2;
		public nint hStdInput;
		public nint hStdOutput;
		public nint hStdError;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CreateProcessW(
		string? lpApplicationName,
		StringBuilder lpCommandLine,
		nint lpProcessAttributes,
		nint lpThreadAttributes,
		[MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
		uint dwCreationFlags,
		nint lpEnvironment,
		string? lpCurrentDirectory,
		ref StartupInfo lpStartupInfo,
		out ProcessInformation lpProcessInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint hObject);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsProcessInJob(nint processHandle, nint jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
