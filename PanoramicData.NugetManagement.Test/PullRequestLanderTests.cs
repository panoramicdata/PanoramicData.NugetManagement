using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// What happens to a governance branch once it is on origin: a pull request, then auto-merge, or a
/// direct merge where GitHub says nothing is left to wait for, or an open pull request for a person.
/// </summary>
public class PullRequestLanderTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string Sha = "0123456789abcdef0123456789abcdef01234567";

	private readonly FakeGitHubWriteApi _github = new();

	private static BranchDelivery Delivery() => new()
	{
		Success = true,
		Branch = "gov/20261128-120000-abc123",
		HeadSha = Sha,
		Subject = "NuGet governance remediation",
		Message = "NuGet governance remediation\n\nRules: COM-01"
	};

	[Fact]
	public async Task WhenAutoMergeIsAccepted_LeavesTheMergeToGitHub()
	{
		var outcome = await PullRequestLander.OpenAndLandAsync(_github, "panoramicdata/Widget", Delivery(), null);

		outcome.Success.Should().BeTrue();
		outcome.PullRequest!.Landing.Should().Be(PullRequestLanding.AutoMergeEnabled);
		outcome.PullRequest.Url.Should().Be("https://github.com/panoramicdata/Widget/pull/7");
		_github.Calls.Should().Equal("pr:gov/20261128-120000-abc123->main", $"automerge:7:{Sha}");
		_github.PullRequestTitle.Should().Be("NuGet governance remediation");
		_github.PullRequestBody.Should().Contain("Rules: COM-01");
	}

	[Fact]
	public async Task WhenGitHubSaysThereIsNothingToWaitFor_SquashMergesTheCheckedHead()
	{
		_github.AutoMerge = new AutoMergeResult(AutoMergeState.AlreadyMergeable, "Pull request is in clean status");

		var outcome = await PullRequestLander.OpenAndLandAsync(_github, "panoramicdata/Widget", Delivery(), null);

		outcome.Success.Should().BeTrue();
		outcome.PullRequest!.Landing.Should().Be(PullRequestLanding.Merged);
		_github.MergedSha.Should().Be(Sha);
	}

	[Fact]
	public async Task WhenTheDirectMergeIsRefused_LeavesThePullRequestOpen()
	{
		_github.AutoMerge = new AutoMergeResult(AutoMergeState.AlreadyMergeable, "Pull request is in clean status");
		_github.MergeThrows = true;

		var outcome = await PullRequestLander.OpenAndLandAsync(_github, "panoramicdata/Widget", Delivery(), null);

		outcome.Success.Should().BeTrue("the change is on a pull request, which is delivery");
		outcome.PullRequest!.Landing.Should().Be(PullRequestLanding.LeftOpen);
		outcome.PullRequest.Note.Should().Contain("Head branch was modified");
	}

	[Fact]
	public async Task WhenAutoMergeIsRefused_LeavesThePullRequestOpenAndSaysWhy()
	{
		_github.AutoMerge = new AutoMergeResult(AutoMergeState.Refused, "Auto merge is not allowed for this repository");
		var lines = new List<string>();

		var outcome = await PullRequestLander.OpenAndLandAsync(_github, "panoramicdata/Widget", Delivery(), lines.Add);

		outcome.PullRequest!.Landing.Should().Be(PullRequestLanding.LeftOpen);
		outcome.PullRequest.Note.Should().Contain("not allowed");
		_github.Calls.Should().NotContain("merge", "a repository with required checks must not be merged around them");
		lines.Should().Contain(line => line.Contains("https://github.com/panoramicdata/Widget/pull/7", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenEnablingAutoMergeThrows_LeavesThePullRequestOpen()
	{
		_github.AutoMergeThrows = true;

		var outcome = await PullRequestLander.OpenAndLandAsync(_github, "panoramicdata/Widget", Delivery(), null);

		outcome.PullRequest!.Landing.Should().Be(PullRequestLanding.LeftOpen);
		_github.Calls.Should().NotContain("merge");
	}

	[Fact]
	public async Task WhenThePullRequestCannotBeOpened_FailsAndNamesTheBranch()
	{
		_github.CreatePullRequestThrows = true;

		var outcome = await PullRequestLander.OpenAndLandAsync(_github, "panoramicdata/Widget", Delivery(), null);

		outcome.Success.Should().BeFalse();
		outcome.PullRequest.Should().BeNull();
		outcome.FailureReason.Should().Contain("gov/20261128-120000-abc123");
	}

	[Theory]
	[InlineData(AutoMergeState.Enabled, PullRequestLanding.AutoMergeEnabled)]
	[InlineData(AutoMergeState.AlreadyMergeable, PullRequestLanding.Merged)]
	[InlineData(AutoMergeState.Refused, PullRequestLanding.LeftOpen)]
	public void Decide_MergesDirectlyOnlyWhenGitHubHasNothingToWaitFor(AutoMergeState state, PullRequestLanding expected)
		=> PullRequestLander.Decide(new AutoMergeResult(state, null)).Should().Be(expected);
}
