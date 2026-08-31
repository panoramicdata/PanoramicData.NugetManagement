using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="TreeReloadPolicy"/>: progress must not rebuild the tree, and the node set must
/// still catch up once the work that changed it has finished.
/// </summary>
public class TreeReloadPolicyTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void ProgressWhileWorkRuns_NeverRebuilds()
	{
		var policy = new TreeReloadPolicy();

		policy.ObserveAndShouldReload(anyRunning: true).Should().BeFalse("the first report merely starts the run");
		policy.ObserveAndShouldReload(anyRunning: true).Should().BeFalse();
		policy.ObserveAndShouldReload(anyRunning: true).Should().BeFalse(
			"a rebuild per progress report is the flicker: it replaces the DOM subtree under every node");
	}

	[Fact]
	public void WhenTheLastLaneFinishes_ItRebuildsOnce()
	{
		var policy = new TreeReloadPolicy();

		policy.ObserveAndShouldReload(anyRunning: true).Should().BeFalse();

		policy.ObserveAndShouldReload(anyRunning: false).Should().BeTrue(
			"the results are in, and the node set they produce may be entirely different");

		policy.ObserveAndShouldReload(anyRunning: false).Should().BeFalse(
			"and only once — nothing has changed since");
	}

	[Fact]
	public void ChangesWithNoWorkRunningAtAll_DoNotRebuild()
		=> new TreeReloadPolicy()
			.ObserveAndShouldReload(anyRunning: false)
			.Should().BeFalse("there was no run to produce new results");

	[Fact]
	public void ASecondRun_RebuildsAgainWhenItFinishes()
	{
		var policy = new TreeReloadPolicy();

		policy.ObserveAndShouldReload(anyRunning: true);
		policy.ObserveAndShouldReload(anyRunning: false).Should().BeTrue();

		policy.ObserveAndShouldReload(anyRunning: true).Should().BeFalse();
		policy.ObserveAndShouldReload(anyRunning: false).Should().BeTrue();
	}

	[Fact]
	public void AWholeBurstAndFinish_RebuildsExactlyOnce()
	{
		var policy = new TreeReloadPolicy();
		var reloads = 0;

		// Twenty lanes reporting repeatedly, then the last one finishing.
		foreach (var running in Enumerable.Repeat(true, 50).Append(false))
		{
			if (policy.ObserveAndShouldReload(running))
			{
				reloads++;
			}
		}

		reloads.Should().Be(1);
	}
}
