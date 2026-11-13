using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;
using PanoramicData.NugetManagement.Web.Services.Governance;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// A repository created after the app started only becomes visible through a refresh, so the agent API must be
/// able to ask for one without a person clicking the button.
/// </summary>
public class AgentApiRefreshTests
{
	[Fact]
	public void ARefreshRequestShouldQueueTheRefreshTheButtonQueues()
	{
		var lanes = new WorkLaneService();

		var item = AgentApi.EnqueueRefresh(lanes);

		item.Should().NotBeNull();
		item!.Descriptor.Kind.Should().Be(WorkKind.RefreshAll);
		item.Descriptor.Organization.Should().BeNull("a refresh covers every organisation");
		item.State.Should().Be(WorkItemState.Pending);
		item.DedupKey.Should().Be("refresh:*", "the dashboard's Refresh button uses the same key, so the two fold into one");
	}

	[Fact]
	public void ASecondRequestWhileOneIsWaitingShouldFoldIntoIt()
	{
		var lanes = new WorkLaneService();

		AgentApi.EnqueueRefresh(lanes).Should().NotBeNull();
		AgentApi.EnqueueRefresh(lanes).Should().BeNull("a pending refresh will see everything that exists when it starts");

		lanes.Lanes.SelectMany(lane => lane.Items).Count(i => i.Descriptor.Kind == WorkKind.RefreshAll)
			.Should().Be(1);
	}

	[Fact]
	public void ARequestWhileOneIsRunningShouldQueueAnotherBehindIt()
	{
		var lanes = new WorkLaneService();
		AgentApi.EnqueueRefresh(lanes).Should().NotBeNull();
		lanes.TryStartNext(out var running).Should().BeTrue();
		running.State.Should().Be(WorkItemState.Running);

		var again = AgentApi.EnqueueRefresh(lanes);

		again.Should().NotBeNull("the running refresh may have listed the organisation before a new repository existed");
		again!.Id.Should().NotBe(running.Id);
	}

	[Fact]
	public void ARefreshQueuedThroughTheApiShouldBeRecordedAsComingFromTheApi()
	{
		var lanes = new WorkLaneService();

		WorkItem? item;
		using (QueueOrigin.Use("agent-api"))
		{
			item = AgentApi.EnqueueRefresh(lanes);
		}

		item!.QueuedBy.Should().Be("agent-api");
	}
}
