using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="TreeReloadPolicy"/>: progress must not rebuild the tree, but the queue's own
/// contents must appear as they change — the work nodes are the one part of the tree whose structure
/// is the information.
/// </summary>
public class TreeReloadPolicyTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void ProgressOnTheSameQueue_NeverRebuilds()
	{
		var policy = new TreeReloadPolicy();
		policy.ObserveAndShouldReload(["a", "b"]);

		policy.ObserveAndShouldReload(["a", "b"]).Should().BeFalse();
		policy.ObserveAndShouldReload(["a", "b"]).Should().BeFalse(
			"a rebuild per progress report is the flicker: it replaces the DOM subtree under every node");
	}

	[Fact]
	public void TheFirstObservationRebuilds_SoTheQueueAppearsAtAll()
		=> new TreeReloadPolicy()
			.ObserveAndShouldReload(["a"])
			.Should().BeTrue();

	[Fact]
	public void WorkBeingQueued_Rebuilds()
	{
		var policy = new TreeReloadPolicy();
		policy.ObserveAndShouldReload(["a"]);

		policy.ObserveAndShouldReload(["a", "b"]).Should().BeTrue(
			"a queued item is a new node, and no amount of re-rendering invents one");
	}

	[Fact]
	public void WorkFinishing_Rebuilds()
	{
		var policy = new TreeReloadPolicy();
		policy.ObserveAndShouldReload(["a", "b"]);

		policy.ObserveAndShouldReload(["a"]).Should().BeTrue("its node has to go, and its results are in");
	}

	[Fact]
	public void TheLastItemFinishing_Rebuilds()
	{
		var policy = new TreeReloadPolicy();
		policy.ObserveAndShouldReload(["a"]);

		policy.ObserveAndShouldReload([]).Should().BeTrue(
			"everything a finished run changed — assessments, counts, findings — lands here");
	}

	[Fact]
	public void OrderOfTheSameItems_IsNotAChange()
	{
		var policy = new TreeReloadPolicy();
		policy.ObserveAndShouldReload(["a", "b"]);

		policy.ObserveAndShouldReload(["b", "a"]).Should().BeFalse(
			"the lanes are snapshotted in whatever order they enumerate; that is not news");
	}

	[Fact]
	public void AQuietQueue_NeverRebuilds()
	{
		var policy = new TreeReloadPolicy();
		policy.ObserveAndShouldReload([]);

		policy.ObserveAndShouldReload([]).Should().BeFalse();
		policy.ObserveAndShouldReload([]).Should().BeFalse();
	}

	[Fact]
	public void ABurstOfProgressThenCompletion_RebuildsOncePerRealChange()
	{
		var policy = new TreeReloadPolicy();
		var reloads = 0;

		// Enqueued, then fifty progress reports, then finished.
		foreach (var queue in Enumerable
			.Repeat<IReadOnlyList<string>>(["a"], 51)
			.Append<IReadOnlyList<string>>([]))
		{
			if (policy.ObserveAndShouldReload(queue))
			{
				reloads++;
			}
		}

		reloads.Should().Be(2, "once when it appeared and once when it went — not fifty-one times");
	}
}
