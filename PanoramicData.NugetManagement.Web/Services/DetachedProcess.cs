using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// A child process started by <see cref="DetachedProcessLauncher"/>.
/// </summary>
/// <remarks>
/// When the child was created through <c>CreateProcessW</c> there is no <see cref="Process"/> object to hand
/// back: a process that exits within milliseconds - as VS Code does when it forwards a folder to an existing
/// instance - can no longer be found by <see cref="Process.GetProcessById(int)"/>. The raw process handle
/// stays valid across that exit, so exit state is read from it directly. Disposing releases the handle; it
/// never terminates the child, which is the entire point of this type.
/// </remarks>
public sealed class DetachedProcess : IDisposable
{
	private const uint StillActive = 259;
	private const uint GwOwner = 4;
	private const uint WaitObject0 = 0;

	private readonly Process? _managed;
	private nint _handle;

	internal DetachedProcess(int id, nint handle)
	{
		Id = id;
		_handle = handle;
	}

	private DetachedProcess(Process managed)
	{
		Id = managed.Id;
		_managed = managed;
	}

	/// <summary>
	/// Wraps a process started through the managed <see cref="Process"/> API.
	/// </summary>
	/// <param name="process">The started process.</param>
	internal static DetachedProcess FromManaged(Process process) => new(process);

	/// <summary>
	/// Gets the process id of the child.
	/// </summary>
	public int Id { get; }

	/// <summary>
	/// Gets a value indicating whether the child has exited.
	/// </summary>
	public bool HasExited
	{
		get
		{
			if (_managed is not null)
			{
				return _managed.HasExited;
			}

			return !GetExitCodeProcess(_handle, out var code) || code != StillActive;
		}
	}

	/// <summary>
	/// Gets the exit code of the child. Only meaningful once <see cref="HasExited"/> is true.
	/// </summary>
	public int ExitCode
	{
		get
		{
			if (_managed is not null)
			{
				return _managed.ExitCode;
			}

			return GetExitCodeProcess(_handle, out var code) ? unchecked((int)code) : 0;
		}
	}

	/// <summary>
	/// Gets the handle of the child's main window, or zero if it does not have a visible one yet.
	/// </summary>
	public nint MainWindowHandle
	{
		get
		{
			if (_managed is not null)
			{
				return _managed.MainWindowHandle;
			}

			return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? FindMainWindow(Id) : nint.Zero;
		}
	}

	/// <summary>
	/// Discards any cached view of the child's state. Native state is read live, so this only affects a
	/// managed <see cref="Process"/>.
	/// </summary>
	public void Refresh() => _managed?.Refresh();

	/// <summary>
	/// Waits up to <paramref name="milliseconds"/> for the child to exit.
	/// </summary>
	/// <param name="milliseconds">Maximum time to wait.</param>
	/// <returns>True if the child has exited.</returns>
	public bool WaitForExit(int milliseconds)
	{
		if (_managed is not null)
		{
			return _managed.WaitForExit(milliseconds);
		}

		return WaitForSingleObject(_handle, (uint)milliseconds) == WaitObject0;
	}

	/// <summary>
	/// Releases this app's handle on the child without terminating it.
	/// </summary>
	public void Dispose()
	{
		_managed?.Dispose();

		if (_handle != nint.Zero)
		{
			_ = CloseHandle(_handle);
			_handle = nint.Zero;
		}
	}

	private static nint FindMainWindow(int processId)
	{
		var found = nint.Zero;

		_ = EnumWindows((handle, lParam) =>
		{
			_ = GetWindowThreadProcessId(handle, out var windowProcessId);
			if (windowProcessId != (uint)processId || !IsWindowVisible(handle) || GetWindow(handle, GwOwner) != nint.Zero)
			{
				return true;
			}

			found = handle;
			return false;
		}, nint.Zero);

		return found;
	}

	private delegate bool EnumWindowsProc(nint windowHandle, nint lParam);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetExitCodeProcess(nint processHandle, out uint exitCode);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint handle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindowVisible(nint windowHandle);

	[DllImport("user32.dll")]
	private static extern nint GetWindow(nint windowHandle, uint command);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
