using Octokit;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="GitHubCheckMapping"/>: how GitHub's check runs and commit statuses become the
/// three states a merge decision needs.
/// </summary>
public class GitHubCheckMappingTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData(CheckStatus.Queued, null, CheckRunConclusion.Pending)]
	[InlineData(CheckStatus.InProgress, null, CheckRunConclusion.Pending)]
	[InlineData(CheckStatus.Completed, CheckConclusion.Success, CheckRunConclusion.Passed)]
	[InlineData(CheckStatus.Completed, CheckConclusion.Neutral, CheckRunConclusion.Passed)]
	[InlineData(CheckStatus.Completed, CheckConclusion.Skipped, CheckRunConclusion.Passed)]
	[InlineData(CheckStatus.Completed, CheckConclusion.Failure, CheckRunConclusion.Failed)]
	[InlineData(CheckStatus.Completed, CheckConclusion.Cancelled, CheckRunConclusion.Failed)]
	[InlineData(CheckStatus.Completed, CheckConclusion.TimedOut, CheckRunConclusion.Failed)]
	[InlineData(CheckStatus.Completed, CheckConclusion.ActionRequired, CheckRunConclusion.Failed)]
	[InlineData(CheckStatus.Completed, CheckConclusion.Stale, CheckRunConclusion.Failed)]
	[InlineData(CheckStatus.Completed, null, CheckRunConclusion.Failed)]
	public void FromCheckRun_MapsEveryStateToPendingPassedOrFailed(
		CheckStatus status, CheckConclusion? conclusion, CheckRunConclusion expected)
		=> GitHubCheckMapping.FromCheckRun(status, conclusion).Should().Be(expected,
			"anything that is not clearly finished and fine is not allowed to merge");

	[Theory]
	[InlineData(CommitState.Pending, CheckRunConclusion.Pending)]
	[InlineData(CommitState.Success, CheckRunConclusion.Passed)]
	[InlineData(CommitState.Failure, CheckRunConclusion.Failed)]
	[InlineData(CommitState.Error, CheckRunConclusion.Failed)]
	public void FromStatus_MapsALegacyCommitStatus(CommitState state, CheckRunConclusion expected)
		=> GitHubCheckMapping.FromStatus(state).Should().Be(expected);
}
