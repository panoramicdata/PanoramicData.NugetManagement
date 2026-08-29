using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Proves that a process spawned for the user - an IDE - escapes the job object this app runs in, so that
/// it survives the app stopping or crashing rather than being killed alongside it.
/// </summary>
public class DetachedProcessLaunchTests
{
	private const uint JobObjectLimitBreakawayOk = 0x0000_0800;
	private const int JobObjectExtendedLimitInformation = 9;

	[Fact]
	public void Start_LaunchesAProcessThatExits()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Assert.Skip("Job objects, and this launch path, are Windows-only.");
		}

		using var process = DetachedProcessLauncher.Start(WhereExePath(), "cmd", null, NullLogger.Instance);

		process.Should().NotBeNull();
		process!.Id.Should().BeGreaterThan(0);
		process.WaitForExit(10_000).Should().BeTrue("the launched process should run and exit");
	}

	[Fact]
	public void Start_FromInsideAJobObject_LaunchesTheChildOutsideIt()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Assert.Skip("Job objects, and this launch path, are Windows-only.");
		}

		if (IsProcessInJob(GetCurrentProcess(), nint.Zero, out var alreadyInJob) && alreadyInJob)
		{
			// An outer job we did not create decides whether breakaway is allowed, so this proves nothing here.
			Assert.Skip("The test process is already in a job object created by its host.");
		}

		var job = CreateJobObjectW(nint.Zero, null);
		job.Should().NotBe(nint.Zero, "the test needs a job object to break out of");

		try
		{
			var limits = new JobObjectExtendedLimitInformationStruct();
			limits.BasicLimitInformation.LimitFlags = JobObjectLimitBreakawayOk;

			var size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
			var buffer = Marshal.AllocHGlobal(size);
			try
			{
				Marshal.StructureToPtr(limits, buffer, false);
				SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size)
					.Should().BeTrue("the job needs the breakaway limit set");
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}

			AssignProcessToJobObject(job, GetCurrentProcess())
				.Should().BeTrue("the test process must join the job to model the app's situation");

			using var process = DetachedProcessLauncher.Start(WhereExePath(), "cmd", null, NullLogger.Instance);
			process.Should().NotBeNull();

			var child = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process!.Id);
			child.Should().NotBe(nint.Zero);

			try
			{
				IsProcessInJob(child, job, out var childInJob).Should().BeTrue();
				childInJob.Should().BeFalse("the child must not inherit the job, or it dies when this app does");
			}
			finally
			{
				_ = CloseHandle(child);
			}
		}
		finally
		{
			// Closing the handle does not kill anything: the job carries no kill-on-close limit.
			_ = CloseHandle(job);
		}
	}

	private static string WhereExePath()
		=> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");

	private const uint ProcessQueryLimitedInformation = 0x1000;

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
	private struct JobObjectBasicLimitInformation
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
	private struct JobObjectExtendedLimitInformationStruct
	{
		public JobObjectBasicLimitInformation BasicLimitInformation;
		public IoCounters IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint CreateJobObjectW(nint securityAttributes, string? name);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint infoLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AssignProcessToJobObject(nint job, nint process);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint handle);
}
