using System.Diagnostics;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// A command the app runs must not leave processes behind: the .NET 11 SDK strands idle MSBuild workers
/// when a build ends badly, and they accumulated by the thousand.
/// </summary>
public class ProcessJobTests
{
	[Fact]
	public void DisposingTwiceClosesTheHandleOnlyOnce()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "ping -n 4 127.0.0.1 >nul"]) { CreateNoWindow = true, UseShellExecute = false })!;
		var job = ProcessJob.TryAssign(process);
		job.Should().NotBeNull();

		job!.Dispose();

		// A reused handle number is what a second CloseHandle would hit; the probe below takes one.
		using var probe = new ManualResetEvent(false);
		job.Dispose();

		probe.SafeWaitHandle.IsClosed.Should().BeFalse("a second dispose must not close another live handle");
		process.WaitForExit();
	}

	[Fact]
	public async Task ACommandThatExitsLeavingAGrandchildRunningHasItReaped()
	{
		// The grandchild is only tracked on Windows, where the job object exists.
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		var baseline = Process.GetProcessesByName("PING").Length;

		// The parent lingers two seconds so the job is in place before the long-lived grandchild is
		// spawned, then exits and orphans it.
		var (success, _) = await LocalRepoService.RunCommandWithStreamingAsync(
			Environment.CurrentDirectory,
			"cmd.exe",
			["/c", "start /b ping -n 7271 127.0.0.1 >nul 2>&1 & ping -n 3 127.0.0.1 >nul"],
			cancellationToken: TestContext.Current.CancellationToken);

		success.Should().BeTrue();

		// Process exit is asynchronous after the job handle closes.
		var current = int.MaxValue;
		for (var attempt = 0; attempt < 20 && current > baseline; attempt++)
		{
			current = Process.GetProcessesByName("PING").Length;
			if (current > baseline)
			{
				await Task.Delay(250, TestContext.Current.CancellationToken);
			}
		}

		current.Should().BeLessThanOrEqualTo(baseline, "closing the job must kill what the command left running");
	}
}
