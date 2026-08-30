using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that work spanning many repositories is decomposed into one item per repository, each in
/// its own lane. This is what lets a bulk apply-and-push across twelve repositories run twelve
/// abreast instead of twelve in a row.
/// </summary>
public class WorkFanOutTests(ITestOutputHelper output) : TestWithOutput(output)
{
	// RepositoryName is computed from RepositoryFullName and has no setter, so only the required
	// RepositoryFullName and the Organization the fan-out reads need setting here.
	private static RepositoryDashboardRow Row(string fullName) => new()
	{
		RepositoryFullName = fullName,
		Organization = fullName.Split('/')[0]
	};

	[Fact]
	public void EnqueueReassess_ThreeRepositories_MakesThreeLanes()
	{
		var lanes = new WorkLaneService();
		var fanOut = new WorkFanOut(lanes);

		var queued = fanOut.EnqueueReassess(
			"panoramicdata",
			[Row("panoramicdata/A"), Row("panoramicdata/B"), Row("panoramicdata/C")],
			consoleNodeKey: null);

		queued.Should().Be(3);
		lanes.Lanes.Should().HaveCount(3);
		lanes.Lanes.Should().OnlyContain(l => l.Items.Count == 1);
	}

	[Fact]
	public void EnqueueReassess_EveryItem_IsRepositoryScopedReassess()
	{
		var lanes = new WorkLaneService();

		new WorkFanOut(lanes).EnqueueReassess("panoramicdata", [Row("panoramicdata/A")], null);

		var item = lanes.ItemsFor("repo:panoramicdata/a").Single();
		item.Descriptor.Kind.Should().Be(WorkKind.Reassess);
		item.Descriptor.RepositoryFullName.Should().Be("panoramicdata/A");
		item.Step.Should().Be(WorkflowStep.Reassess);
	}

	[Fact]
	public void EnqueueRule_WithPush_QueuesFixThenCommitAndPushInEachLane()
	{
		var lanes = new WorkLaneService();

		new WorkFanOut(lanes).EnqueueRule(
			"panoramicdata", "TST-06", ["panoramicdata/A", "panoramicdata/B"], push: true, consoleNodeKey: null);

		foreach (var laneKey in new[] { "repo:panoramicdata/a", "repo:panoramicdata/b" })
		{
			var items = lanes.ItemsFor(laneKey);
			items.Should().HaveCount(2);
			items[0].Descriptor.Kind.Should().Be(WorkKind.FixRule);
			items[0].Descriptor.Parameter("ruleId").Should().Be("TST-06");
			items[1].Descriptor.Kind.Should().Be(WorkKind.CommitAndPush);
		}
	}

	[Fact]
	public void EnqueueRule_CalledTwiceForTheSameRepositoryAndRule_StillQueuesAPushBehindEachFix()
	{
		var lanes = new WorkLaneService();
		var fanOut = new WorkFanOut(lanes);

		fanOut.EnqueueRule("panoramicdata", "TST-06", ["panoramicdata/A"], push: true, consoleNodeKey: null);

		// The fix runs to completion and leaves the lane while its push is still pending behind it —
		// the exact cross-call shape a re-assessment sweep, or a double-click, produces.
		lanes.TryStartNext(out var runningFix).Should().BeTrue();
		runningFix.Descriptor.Kind.Should().Be(WorkKind.FixRule);
		lanes.Complete(runningFix, error: null);

		// A second sweep re-detects the same violation on the same repository.
		fanOut.EnqueueRule("panoramicdata", "TST-06", ["panoramicdata/A"], push: true, consoleNodeKey: null);

		var kinds = lanes.ItemsFor("repo:panoramicdata/a").Select(i => i.Descriptor.Kind);

		kinds.Should().Equal(
			[WorkKind.CommitAndPush, WorkKind.FixRule, WorkKind.CommitAndPush],
			"a repository must never be left fixed and unpushed");
	}

	[Fact]
	public void EnqueueRule_WithoutPush_QueuesOnlyTheFix()
	{
		var lanes = new WorkLaneService();

		new WorkFanOut(lanes).EnqueueRule("panoramicdata", "TST-06", ["panoramicdata/A"], push: false, null);

		lanes.ItemsFor("repo:panoramicdata/a").Should().ContainSingle()
			.Which.Descriptor.Kind.Should().Be(WorkKind.FixRule);
	}

	[Fact]
	public void EnqueueReassess_TheSameRepositoryTwice_IsFoldedIntoOne()
	{
		var lanes = new WorkLaneService();

		new WorkFanOut(lanes).EnqueueReassess("panoramicdata", [Row("panoramicdata/A"), Row("panoramicdata/A")], null);

		lanes.ItemsFor("repo:panoramicdata/a").Should().ContainSingle();
	}

	[Fact]
	public void EnqueueReassess_NoRepositories_QueuesNothing()
	{
		var lanes = new WorkLaneService();

		new WorkFanOut(lanes).EnqueueReassess("panoramicdata", [], null).Should().Be(0);
		lanes.Lanes.Should().BeEmpty();
	}
}
