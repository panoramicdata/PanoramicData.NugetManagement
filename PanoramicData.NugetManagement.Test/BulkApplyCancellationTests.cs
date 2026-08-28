using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for what stopping a bulk apply does to the repository it was part-way through. A change is
/// atomic per repository: anything short of the commit is undone, and anything past it stands.
/// </summary>
public class BulkApplyCancellationTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData(RepoApplyPhase.NotStarted, false)]
	[InlineData(RepoApplyPhase.Applying, true)]
	[InlineData(RepoApplyPhase.Applied, true)]
	[InlineData(RepoApplyPhase.Pushed, false)]
	public void NeedsRevert_IsTrueOnlyBetweenTheFirstEditAndTheCommit(RepoApplyPhase phase, bool expected)
		=> BulkApplyCancellation.NeedsRevert(phase).Should().Be(expected);

	[Fact]
	public void Describe_ShouldReportAnUntouchedRepositoryAsSkipped()
	{
		var result = BulkApplyCancellation.Describe("acme/Widget", RepoApplyPhase.NotStarted);

		result.Status.Should().Be(RepoApplyStatus.Skipped);
		result.RepositoryFullName.Should().Be("acme/Widget");
	}

	[Theory]
	[InlineData(RepoApplyPhase.Applying)]
	[InlineData(RepoApplyPhase.Applied)]
	public void Describe_ShouldReportAHalfAppliedRepositoryAsReverted(RepoApplyPhase phase)
		=> BulkApplyCancellation.Describe("acme/Widget", phase).Status.Should().Be(RepoApplyStatus.Reverted);

	[Fact]
	public void Describe_ShouldReportAPushedRepositoryAsPushed()
		=> BulkApplyCancellation.Describe("acme/Widget", RepoApplyPhase.Pushed).Status
			.Should().Be(RepoApplyStatus.Pushed, "a pushed change is done, not half-done");

	[Fact]
	public void RevertedCount_ShouldCountRevertedRepositoriesSeparatelyFromFailures()
	{
		var outcome = new BulkApplyOutcome();
		outcome.Results.Add(BulkApplyCancellation.Describe("acme/One", RepoApplyPhase.Applied));
		outcome.Results.Add(BulkApplyCancellation.Describe("acme/Two", RepoApplyPhase.NotStarted));
		outcome.Results.Add(new RepoApplyResult
		{
			RepositoryFullName = "acme/Three",
			Status = RepoApplyStatus.Failed,
			Message = "boom"
		});

		outcome.RevertedCount.Should().Be(1);
		outcome.SkippedCount.Should().Be(1);
		outcome.FailedCount.Should().Be(1);
	}
}
