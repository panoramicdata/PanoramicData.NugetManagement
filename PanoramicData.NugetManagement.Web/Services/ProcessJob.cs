using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// A Windows job object that kills every process still in it when disposed.
/// </summary>
/// <remarks>
/// The .NET 11 SDK leaves idle MSBuild worker processes behind when a build ends badly; thousands of them
/// accumulated, holding tens of gigabytes. Children inherit their parent's job membership, so disposing the
/// job reaps them even after the process that started them has exited.
/// </remarks>
internal sealed class ProcessJob : IDisposable
{
	private const int JobObjectExtendedLimitInformation = 9;
	private const uint JobObjectLimitKillOnJobClose = 0x2000;

	private nint _handle;

	private ProcessJob(nint handle) => _handle = handle;

	/// <summary>
	/// Puts the process in a new job, or returns null where that is not possible: off Windows, or when the
	/// process has already exited.
	/// </summary>
	/// <param name="process">The process just started.</param>
	public static ProcessJob? TryAssign(Process process)
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		var handle = CreateJobObject(nint.Zero, null);
		if (handle == nint.Zero)
		{
			return null;
		}

		var limits = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
		if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>())
			|| !AssignProcessToJobObject(handle, process.Handle))
		{
			CloseHandle(handle);
			return null;
		}

		return new ProcessJob(handle);
	}

	/// <inheritdoc />
	/// <remarks>
	/// Safe to call twice. Windows reuses handle numbers, so a second CloseHandle can close an unrelated live
	/// handle and crash the process.
	/// </remarks>
	public void Dispose()
	{
		var handle = Interlocked.Exchange(ref _handle, nint.Zero);
		if (handle != nint.Zero)
		{
			CloseHandle(handle);
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static extern nint CreateJobObject(nint attributes, string? name);

	[DllImport("kernel32.dll", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimitInformation information, uint length);

	[DllImport("kernel32.dll", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AssignProcessToJobObject(nint job, nint process);

	[DllImport("kernel32.dll", SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint handle);

	[StructLayout(LayoutKind.Sequential)]
	private struct BasicLimitInformation
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public nuint MinimumWorkingSetSize;
		public nuint MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public nuint Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IoCounters
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ExtendedLimitInformation
	{
		public BasicLimitInformation BasicLimitInformation;
		public IoCounters IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}
}
