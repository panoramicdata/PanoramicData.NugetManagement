using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the toolbar's queue gating: once a workflow step is queued for a repository, that step
/// and everything downstream of it are no longer offered, because their preconditions are about to
/// change. Steps upstream stay available so they can be queued behind.
/// </summary>
public class WorkflowGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string Repo = "panoramicdata/Athonet.Api";

	private static WorkItem Item(WorkflowStep? step, string? repositoryFullName) => new()
	{
		Id = $"{step}",
		Title = $"{step} {repositoryFullName}",
		DedupKey = $"{step}:{repositoryFullName}",
		OwnerId = new object(),
		RepositoryFullName = repositoryFullName,
		Step = step,
		Run = (_, _) => Task.CompletedTask
	};

	[Fact]
	public void FirstBlockedStep_EmptyQueue_BlocksNothing()
		=> WorkflowGate.FirstBlockedStep([], Repo).Should().BeNull();

	[Fact]
	public void FirstBlockedStep_QueuedStep_IsItself()
		=> WorkflowGate.FirstBlockedStep([Item(WorkflowStep.Fix, Repo)], Repo)
			.Should().Be(WorkflowStep.Fix);

	[Fact]
	public void FirstBlockedStep_SeveralQueued_IsTheEarliestWhateverTheQueueOrder()
		=> WorkflowGate.FirstBlockedStep(
				[Item(WorkflowStep.Publish, Repo), Item(WorkflowStep.Build, Repo), Item(WorkflowStep.Test, Repo)],
				Repo)
			.Should().Be(WorkflowStep.Build);

	[Fact]
	public void FirstBlockedStep_AnotherRepositorysWork_BlocksNothingHere()
		=> WorkflowGate.FirstBlockedStep([Item(WorkflowStep.Fix, "panoramicdata/Auvik.Api")], Repo)
			.Should().BeNull();

	[Fact]
	public void FirstBlockedStep_WorkScopedToNoRepository_BlocksNothing()
		=> WorkflowGate.FirstBlockedStep([Item(WorkflowStep.Reassess, null)], Repo).Should().BeNull();

	[Fact]
	public void FirstBlockedStep_NoRepositorySelected_BlocksNothing()
		=> WorkflowGate.FirstBlockedStep([Item(WorkflowStep.Fix, Repo)], null).Should().BeNull();

	[Fact]
	public void FirstBlockedStep_ItemWithNoStep_IsIgnored()
		=> WorkflowGate.FirstBlockedStep([Item(null, Repo)], Repo).Should().BeNull();

	[Fact]
	public void FirstBlockedStep_RepositoryNameCasing_StillMatches()
		=> WorkflowGate.FirstBlockedStep([Item(WorkflowStep.Build, "PanoramicData/athonet.api")], Repo)
			.Should().Be(WorkflowStep.Build);

	[Fact]
	public void IsBlocked_TheQueuedStepItself_IsBlocked()
		=> WorkflowGate.IsBlocked(WorkflowStep.Fix, WorkflowStep.Fix).Should().BeTrue();

	[Fact]
	public void IsBlocked_DownstreamOfTheQueuedStep_IsBlocked()
	{
		WorkflowGate.IsBlocked(WorkflowStep.Build, WorkflowStep.Fix).Should().BeTrue();
		WorkflowGate.IsBlocked(WorkflowStep.Publish, WorkflowStep.Fix).Should().BeTrue();
	}

	[Fact]
	public void IsBlocked_UpstreamOfTheQueuedStep_StaysAvailable()
	{
		WorkflowGate.IsBlocked(WorkflowStep.GitSync, WorkflowStep.Fix).Should().BeFalse();
		WorkflowGate.IsBlocked(WorkflowStep.Reassess, WorkflowStep.Fix).Should().BeFalse();
	}

	[Fact]
	public void IsBlocked_NothingQueued_NothingBlocked()
		=> WorkflowGate.IsBlocked(WorkflowStep.Publish, null).Should().BeFalse();

	[Fact]
	public void IsBlocked_FixWithAi_GatesWithFix()
	{
		// Fix and Fix with AI are the same point in the workflow: one remediates automatically, the
		// other by prompt. Queueing either has to gate the other, or a repository could be fixed twice.
		WorkflowGate.IsBlocked(WorkflowStep.FixWithAi, WorkflowStep.Fix).Should().BeTrue();
		WorkflowGate.IsBlocked(WorkflowStep.Fix, WorkflowStep.FixWithAi).Should().BeTrue();
	}
}
