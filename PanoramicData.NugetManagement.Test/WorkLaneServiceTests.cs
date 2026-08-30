using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the lane queue. What matters here is the pair of invariants the design turns on: one
/// item at a time within a lane, and many lanes at a time across the estate, bounded by a cap.
/// </summary>
public class WorkLaneServiceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string RepoA = "panoramicdata/Athonet.Api";
	private const string RepoB = "panoramicdata/Auvik.Api";

	private static WorkLaneService NewService(int maxConcurrentLanes = 20)
		=> new() { MaxConcurrentLanes = maxConcurrentLanes };

	private static WorkItem Enqueue(WorkLaneService service, string repository, WorkKind kind = WorkKind.Build)
		=> service.Enqueue(
			$"{kind} {repository}",
			WorkDescriptor.ForRepository(kind, "panoramicdata", repository),
			$"{kind}:{repository}",
			step: null,
			consoleNodeKey: null)!;

	[Fact]
	public void Enqueue_TwoRepositories_MakesTwoLanes()
	{
		var service = NewService();

		Enqueue(service, RepoA);
		Enqueue(service, RepoB);

		service.Lanes.Should().HaveCount(2);
	}

	[Fact]
	public void TryStartNext_TwoLanes_StartsBoth()
	{
		var service = NewService();
		Enqueue(service, RepoA);
		Enqueue(service, RepoB);

		service.TryStartNext(out var first).Should().BeTrue();
		service.TryStartNext(out var second).Should().BeTrue();

		first.RepositoryFullName.Should().NotBe(second.RepositoryFullName);
		service.RunningLaneCount.Should().Be(2);
	}

	[Fact]
	public void TryStartNext_SameLaneTwice_StartsOnlyTheFirst()
	{
		var service = NewService();
		Enqueue(service, RepoA, WorkKind.Build);
		Enqueue(service, RepoA, WorkKind.Test);

		service.TryStartNext(out var first).Should().BeTrue();
		service.TryStartNext(out _).Should().BeFalse();

		first.Descriptor.Kind.Should().Be(WorkKind.Build);
	}

	[Fact]
	public void TryStartNext_LaneFinishes_NextItemInThatLaneStarts()
	{
		var service = NewService();
		Enqueue(service, RepoA, WorkKind.Build);
		Enqueue(service, RepoA, WorkKind.Test);
		service.TryStartNext(out var first);

		service.Complete(first, error: null);

		service.TryStartNext(out var second).Should().BeTrue();
		second.Descriptor.Kind.Should().Be(WorkKind.Test);
	}

	[Fact]
	public void TryStartNext_AtTheCap_StartsNoFurtherLane()
	{
		var service = NewService(maxConcurrentLanes: 1);
		Enqueue(service, RepoA);
		Enqueue(service, RepoB);

		service.TryStartNext(out _).Should().BeTrue();
		service.TryStartNext(out _).Should().BeFalse();
		service.RunningLaneCount.Should().Be(1);
	}

	[Fact]
	public void TryStartNext_CapRaised_PromotesTheWaitingLane()
	{
		var service = NewService(maxConcurrentLanes: 1);
		Enqueue(service, RepoA);
		Enqueue(service, RepoB);
		service.TryStartNext(out _);

		service.MaxConcurrentLanes = 2;

		service.TryStartNext(out _).Should().BeTrue();
	}

	[Fact]
	public void Enqueue_IdenticalPendingItemInSameLane_IsFoldedIn()
	{
		var service = NewService();
		Enqueue(service, RepoA);

		var second = service.Enqueue(
			"Build again",
			WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", RepoA),
			$"{WorkKind.Build}:{RepoA}",
			step: null,
			consoleNodeKey: null);

		second.Should().BeNull();
		service.ItemsFor($"repo:{RepoA.ToLowerInvariant()}").Should().HaveCount(1);
	}

	[Fact]
	public void Enqueue_SameDedupKeyInAnotherLane_IsNotFoldedIn()
	{
		var service = NewService();
		service.Enqueue("Build A", WorkDescriptor.ForRepository(WorkKind.Build, "o", RepoA), "build", null, null);

		var second = service.Enqueue("Build B", WorkDescriptor.ForRepository(WorkKind.Build, "o", RepoB), "build", null, null);

		second.Should().NotBeNull();
	}

	[Fact]
	public void Enqueue_MatchingTheRunningItem_IsQueuedRatherThanFolded()
	{
		var service = NewService();
		Enqueue(service, RepoA);
		service.TryStartNext(out _);

		var second = service.Enqueue(
			"Build again",
			WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", RepoA),
			$"{WorkKind.Build}:{RepoA}",
			step: null,
			consoleNodeKey: null);

		second.Should().NotBeNull("the running item may already be returning a stale picture");
	}

	[Fact]
	public void Cancel_PendingItem_RemovesIt()
	{
		var service = NewService();
		Enqueue(service, RepoA, WorkKind.Build);
		var pending = Enqueue(service, RepoA, WorkKind.Test);

		service.Cancel(pending.Id);

		service.ItemsFor(pending.LaneKey).Should().ContainSingle();
	}

	[Fact]
	public void Cancel_RunningItem_SignalsItRatherThanDroppingIt()
	{
		var service = NewService();
		var item = Enqueue(service, RepoA);
		service.TryStartNext(out _);

		service.Cancel(item.Id);

		item.State.Should().Be(WorkItemState.Cancelling);
		service.Token(item.Id)!.Value.IsCancellationRequested.Should().BeTrue();
	}

	[Fact]
	public void CancelUnder_Organization_StopsEveryLaneBeneathIt()
	{
		var service = NewService();
		Enqueue(service, RepoA);
		Enqueue(service, RepoB);
		service.Enqueue("Other org", WorkDescriptor.ForRepository(WorkKind.Build, "other", "other/Thing"), "k", null, null);

		service.CancelUnder("panoramicdata");

		service.ItemsFor($"repo:{RepoA.ToLowerInvariant()}").Should().BeEmpty();
		service.ItemsFor($"repo:{RepoB.ToLowerInvariant()}").Should().BeEmpty();
		service.ItemsFor("repo:other/thing").Should().ContainSingle();
	}

	[Fact]
	public void Complete_LastItemInLane_RemovesTheLane()
	{
		var service = NewService();
		var item = Enqueue(service, RepoA);
		service.TryStartNext(out _);

		service.Complete(item, error: null);

		service.Lanes.Should().BeEmpty("an empty lane is not a lane, and would show as an empty node");
	}

	[Fact]
	public void Changed_OnEnqueue_IsRaised()
	{
		var service = NewService();
		var raised = 0;
		service.Changed += () => raised++;

		Enqueue(service, RepoA);

		raised.Should().Be(1);
	}
}
