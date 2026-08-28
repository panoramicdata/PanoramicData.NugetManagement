using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the application-wide work queue: one item runs at a time, duplicates of pending work are
/// folded together, and work belonging to a departed circuit does not stall the queue.
/// </summary>
public class WorkQueueServiceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static Task Nothing(IProgress<string> progress, CancellationToken cancellationToken) => Task.CompletedTask;

	[Fact]
	public void Enqueue_ShouldQueueTheSecondItemBehindTheFirst()
	{
		var queue = new WorkQueueService();
		var owner = new object();

		queue.Enqueue("First", "org", "first", owner, Nothing);
		queue.Enqueue("Second", "org", "second", owner, Nothing);

		queue.TryDequeueForExecution(owner, out var running).Should().BeTrue();
		running.Title.Should().Be("First");
		queue.TryDequeueForExecution(owner, out _).Should().BeFalse("only one item runs at a time");
		queue.Items.Should().HaveCount(2);
	}

	[Fact]
	public void Enqueue_ShouldFoldADuplicateOfAPendingItem()
	{
		var queue = new WorkQueueService();
		var owner = new object();

		queue.Enqueue("Re-assess org", "org", "reassess:org", owner, Nothing).Should().NotBeNull();
		queue.Enqueue("Re-assess org", "org", "reassess:org", owner, Nothing).Should().BeNull();

		queue.Items.Should().ContainSingle();
	}

	[Fact]
	public void Enqueue_ShouldNotFoldADuplicateOfTheRunningItem()
	{
		var queue = new WorkQueueService();
		var owner = new object();
		queue.Enqueue("Re-assess org", "org", "reassess:org", owner, Nothing);
		queue.TryDequeueForExecution(owner, out _);

		queue.Enqueue("Re-assess org", "org", "reassess:org", owner, Nothing)
			.Should().NotBeNull("the running pass may already be returning a stale picture");

		queue.Items.Should().HaveCount(2);
	}

	[Fact]
	public void Complete_ShouldLetTheNextItemRun()
	{
		var queue = new WorkQueueService();
		var owner = new object();
		queue.Enqueue("First", null, "first", owner, Nothing);
		queue.Enqueue("Second", null, "second", owner, Nothing);
		queue.TryDequeueForExecution(owner, out var first);

		queue.Complete(first, null);

		first.State.Should().Be(WorkItemState.Completed);
		queue.TryDequeueForExecution(owner, out var second).Should().BeTrue();
		second.Title.Should().Be("Second");
		queue.Items.Should().ContainSingle();
	}

	[Fact]
	public void Complete_ShouldRecordAFailureWhenTheWorkThrew()
	{
		var queue = new WorkQueueService();
		var owner = new object();
		queue.Enqueue("First", null, "first", owner, Nothing);
		queue.TryDequeueForExecution(owner, out var running);

		queue.Complete(running, new InvalidOperationException("boom"));

		running.State.Should().Be(WorkItemState.Failed);
		queue.Items.Should().BeEmpty();
		queue.Running.Should().BeNull();
	}

	[Fact]
	public void Cancel_ShouldSignalTheRunningItemsToken()
	{
		var queue = new WorkQueueService();
		var owner = new object();
		queue.Enqueue("Long run", null, "long", owner, Nothing);
		queue.TryDequeueForExecution(owner, out var running);

		queue.Cancel(running.Id);

		queue.Token(running.Id)!.Value.IsCancellationRequested.Should().BeTrue();
		running.State.Should().Be(WorkItemState.Cancelling);
		queue.Items.Should().ContainSingle("the item stays visible while it unwinds and reverts");
	}

	[Fact]
	public void Complete_ShouldRecordACancelledItemAsCancelled()
	{
		var queue = new WorkQueueService();
		var owner = new object();
		queue.Enqueue("Long run", null, "long", owner, Nothing);
		queue.TryDequeueForExecution(owner, out var running);
		queue.Cancel(running.Id);

		queue.Complete(running, new OperationCanceledException());

		running.State.Should().Be(WorkItemState.Cancelled);
		queue.Running.Should().BeNull();
	}

	[Fact]
	public void Remove_ShouldDropAPendingItemButNotTheRunningOne()
	{
		var queue = new WorkQueueService();
		var owner = new object();
		queue.Enqueue("First", null, "first", owner, Nothing);
		var pending = queue.Enqueue("Second", null, "second", owner, Nothing);
		queue.TryDequeueForExecution(owner, out var running);

		queue.Remove(pending!.Id);
		queue.Remove(running.Id);

		queue.Items.Should().ContainSingle().Which.Id.Should().Be(running.Id);
	}

	[Fact]
	public void RemoveOwnedBy_ShouldDropPendingWorkAndStopRunningWork()
	{
		var queue = new WorkQueueService();
		var leaving = new object();
		var staying = new object();
		queue.Enqueue("Leaving runs", null, "a", leaving, Nothing);
		queue.Enqueue("Leaving pending", null, "b", leaving, Nothing);
		queue.Enqueue("Staying pending", null, "c", staying, Nothing);
		queue.TryDequeueForExecution(leaving, out var running);

		queue.RemoveOwnedBy(leaving);

		running.State.Should().Be(WorkItemState.Cancelling);
		queue.Items.Should().HaveCount(2, "the running item stays until its revert finishes");
		queue.Items.Should().NotContain(i => i.Title == "Leaving pending");
	}

	[Fact]
	public void TryDequeueForExecution_ShouldOnlyOfferItemsOwnedByTheCaller()
	{
		var queue = new WorkQueueService();
		var owner = new object();
		var other = new object();
		queue.Enqueue("Theirs", null, "theirs", other, Nothing);

		queue.TryDequeueForExecution(owner, out _).Should().BeFalse();
		queue.TryDequeueForExecution(other, out var item).Should().BeTrue();
		item.Title.Should().Be("Theirs");
	}

	[Fact]
	public void TryDequeueForExecution_ShouldNotLetALaterItemJumpTheHead()
	{
		var queue = new WorkQueueService();
		var first = new object();
		var second = new object();
		queue.Enqueue("Theirs, first", null, "a", first, Nothing);
		queue.Enqueue("Mine, second", null, "b", second, Nothing);

		queue.TryDequeueForExecution(second, out _)
			.Should().BeFalse("the visible order must be the order things run in");
	}

	[Fact]
	public void Changed_ShouldFireAsTheQueueMoves()
	{
		var queue = new WorkQueueService();
		var owner = new object();
		var fired = 0;
		queue.Changed += () => fired++;

		queue.Enqueue("One", null, "one", owner, Nothing);
		queue.TryDequeueForExecution(owner, out var running);
		queue.ReportProgress(running, "repo 1 of 3");
		queue.Complete(running, null);

		fired.Should().Be(4);
	}

	[Fact]
	public void ReportProgress_ShouldRecordProgressAgainstTheItem()
	{
		var queue = new WorkQueueService();
		var owner = new object();
		queue.Enqueue("One", null, "one", owner, Nothing);
		queue.TryDequeueForExecution(owner, out var running);

		queue.ReportProgress(running, "repo 8 of 47");

		running.Progress.Should().Be("repo 8 of 47");
	}
}
