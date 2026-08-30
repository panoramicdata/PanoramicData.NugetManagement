using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that queued work survives a restart, and that work interrupted mid-run comes back marked
/// so its half-applied changes can be cleaned up before it runs again.
/// </summary>
public class WorkPersistenceTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _path = Path.Combine(
		Path.GetTempPath(),
		$"work-queue-test-{Guid.NewGuid():N}.json");

	private const string Repo = "panoramicdata/Athonet.Api";

	public void Dispose()
	{
		foreach (var file in new[] { _path, _path + ".tmp" })
		{
			if (File.Exists(file))
			{
				File.Delete(file);
			}
		}

		GC.SuppressFinalize(this);
	}

	private WorkLaneService ServiceWithBuildAndTestQueued()
	{
		var service = new WorkLaneService();
		service.Enqueue("Build", WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo), "build", WorkflowStep.Build, "node");
		service.Enqueue("Test", WorkDescriptor.ForRepository(WorkKind.Test, "panoramicdata", Repo), "test", WorkflowStep.Test, "node");
		return service;
	}

	[Fact]
	public void Load_NothingSaved_IsEmpty()
		=> new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance).Load().Should().BeEmpty();

	[Fact]
	public void SaveThenLoad_PendingItems_ComeBack()
	{
		var store = new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance);
		store.Save(ServiceWithBuildAndTestQueued().Snapshot());

		var loaded = store.Load();

		loaded.Should().HaveCount(2);
		loaded[0].Descriptor.Kind.Should().Be(WorkKind.Build);
		loaded[0].Step.Should().Be(WorkflowStep.Build);
		loaded[0].ConsoleNodeKey.Should().Be("node");
		loaded[1].Descriptor.Kind.Should().Be(WorkKind.Test);
	}

	[Fact]
	public void Snapshot_RunningItem_IsRecordedAsHavingBeenRunning()
	{
		var service = ServiceWithBuildAndTestQueued();
		service.TryStartNext(out _);

		var snapshot = service.Snapshot();

		snapshot.Should().HaveCount(2);
		snapshot[0].WasRunning.Should().BeTrue();
		snapshot[1].WasRunning.Should().BeFalse();
	}

	[Fact]
	public void Restore_ItemThatWasRunning_ComesBackPendingAndInterrupted()
	{
		var store = new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance);
		var service = ServiceWithBuildAndTestQueued();
		service.TryStartNext(out _);
		store.Save(service.Snapshot());

		var restored = new WorkLaneService();
		restored.Restore(store.Load());

		var items = restored.ItemsFor($"repo:{Repo.ToLowerInvariant()}");
		items.Should().HaveCount(2);
		items[0].State.Should().Be(WorkItemState.Pending, "nothing resumes mid-run");
		items[0].WasInterrupted.Should().BeTrue();
		items[1].WasInterrupted.Should().BeFalse();
	}

	[Fact]
	public void Restore_RebuildsTheLanes()
	{
		var store = new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance);
		var service = new WorkLaneService();
		service.Enqueue("A", WorkDescriptor.ForRepository(WorkKind.Build, "o", "o/A"), "a", null, null);
		service.Enqueue("B", WorkDescriptor.ForRepository(WorkKind.Build, "o", "o/B"), "b", null, null);
		store.Save(service.Snapshot());

		var restored = new WorkLaneService();
		restored.Restore(store.Load());

		restored.Lanes.Should().HaveCount(2);
	}

	[Fact]
	public void Load_CorruptFile_IsEmptyRatherThanThrowing()
	{
		File.WriteAllText(_path, "{ this is not json");

		new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance).Load().Should().BeEmpty(
			"a queue file that cannot be read must not stop the application starting");
	}

	[Fact]
	public void Load_EmptyFile_IsEmptyRatherThanThrowing()
	{
		File.WriteAllText(_path, string.Empty);

		new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance).Load().Should().BeEmpty(
			"a partially-written or truncated queue file must not stop the application starting");
	}

	[Fact]
	public void Snapshot_ItemTheUserStopped_IsNotSaved()
	{
		// A Cancelling item is work the user has explicitly stopped; it is only still in its lane
		// because it has yet to unwind. Saving it would resurrect it on the next start — reviving,
		// with WasRunning set, the one outcome the user has already ruled out.
		var service = ServiceWithBuildAndTestQueued();
		service.TryStartNext(out var running);
		service.Cancel(running.Id);

		var snapshot = service.Snapshot();

		snapshot.Should().ContainSingle("only the untouched pending item is still owed")
			.Which.Descriptor.Kind.Should().Be(WorkKind.Test);
	}

	[Fact]
	public void Save_LeavesNoTemporaryFileBehind()
	{
		// The save is written to a sibling and moved into place, because File.WriteAllText truncates
		// before it fills: a process killed between the two leaves a file that parses as nothing, and
		// Load — which must not throw on a file it cannot read — would then silently discard every
		// pending item. Surviving a kill is the whole point of the store.
		var store = new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance);
		store.Save(ServiceWithBuildAndTestQueued().Snapshot());

		File.Exists(_path + ".tmp").Should().BeFalse();
		store.Load().Should().HaveCount(2);
	}

	[Fact]
	public void Save_OverAnExistingQueue_ReplacesItWholesale()
	{
		var store = new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance);
		store.Save(ServiceWithBuildAndTestQueued().Snapshot());

		var replacement = new WorkLaneService();
		replacement.Enqueue("Publish", WorkDescriptor.ForRepository(WorkKind.Publish, "panoramicdata", Repo), "publish", null, null);
		store.Save(replacement.Snapshot());

		store.Load().Should().ContainSingle().Which.Descriptor.Kind.Should().Be(WorkKind.Publish);
	}

	[Fact]
	public void Save_AStaleTemporaryFileFromAKilledWrite_DoesNotStopTheNextSave()
	{
		// What a kill mid-write actually leaves behind: a truncated sibling. The real file is still
		// whole — that is the point — and the next save must simply overwrite the sibling rather than
		// failing because it is already there.
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		File.WriteAllText(_path + ".tmp", "[ { \"Title\": \"half-writ");

		var store = new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance);
		store.Save(ServiceWithBuildAndTestQueued().Snapshot());

		store.Load().Should().HaveCount(2);
	}

	[Fact]
	public void Restore_RunningAndPendingItemSharingADedupKey_KeepsBoth()
	{
		var store = new WorkQueueStore(_path, NullLogger<WorkQueueStore>.Instance);
		var service = new WorkLaneService();
		var descriptor = WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", Repo);

		var first = service.Enqueue("Build", descriptor, "build", WorkflowStep.Build, "node");
		service.TryStartNext(out _);

		// This is the precondition the bug relied on: Enqueue's fold only matches PENDING items, so
		// a second request sharing the running item's dedup key is legitimately queued rather than
		// swallowed — asking again while the first pass is stale earns a fresh pass.
		var second = service.Enqueue("Build", descriptor, "build", WorkflowStep.Build, "node");
		first.Should().NotBeNull();
		second.Should().NotBeNull("a request sharing a dedup key with a RUNNING item is not folded");

		store.Save(service.Snapshot());

		var restored = new WorkLaneService();
		restored.Restore(store.Load());

		var items = restored.ItemsFor($"repo:{Repo.ToLowerInvariant()}");
		items.Should().HaveCount(2,
			"work queued behind a running item must not be folded away by a restart");
	}
}
