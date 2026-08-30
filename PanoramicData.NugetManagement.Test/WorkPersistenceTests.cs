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
		if (File.Exists(_path))
		{
			File.Delete(_path);
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
}
