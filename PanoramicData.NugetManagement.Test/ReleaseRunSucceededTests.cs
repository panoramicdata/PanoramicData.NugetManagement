using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for CI-13, which reports a release run that did not succeed.
/// </summary>
/// <remarks>
/// CI-11 can see that a tag never reached nuget.org but not why, and it has to stay quiet while a
/// release is still in flight — which left a run that failed thirty seconds ago reported by nothing
/// at all. This rule reads the run for the newest tag and says what happened to it.
/// </remarks>
public class ReleaseRunSucceededTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task ShouldFail_WhenTheRunForTheNewestTagFailed()
	{
		var context = CreateContext(
			latestTag: "2.196.75",
			releaseRun: Run(ReleaseRunStatus.Completed, ReleaseRunConclusion.Failure));

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("2.196.75").And.Contain("33757069381");
	}

	[Fact]
	public async Task ShouldFail_WhenTheRunWasCancelled()
	{
		var context = CreateContext(
			latestTag: "2.196.75",
			releaseRun: Run(ReleaseRunStatus.Completed, ReleaseRunConclusion.Cancelled));

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse("a cancelled release published nothing, whoever cancelled it");
	}

	[Fact]
	public async Task ShouldFail_WhenTheRunWasRefusedBeforeAnyStepRan()
	{
		// An exhausted Actions budget fails a run with no failed step at all: the conclusion is the
		// only place it shows.
		var context = CreateContext(
			latestTag: "2.196.75",
			releaseRun: Run(ReleaseRunStatus.Completed, ReleaseRunConclusion.Other));

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldCarryTheRunUrl_SoTheCauseCanBeRead()
	{
		var context = CreateContext(
			latestTag: "2.196.75",
			releaseRun: Run(ReleaseRunStatus.Completed, ReleaseRunConclusion.Failure));

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Advisory.Should().NotBeNull();
		result.Advisory!.Detail.Should().Contain("33757069381");
		result.Advisory.Data.Should().ContainKey("run_url");
	}

	[Fact]
	public async Task ShouldPass_WhenTheRunSucceeded()
	{
		var context = CreateContext(
			latestTag: "2.196.75",
			releaseRun: Run(ReleaseRunStatus.Completed, ReleaseRunConclusion.Success));

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldNotApply_WhileTheRunIsStillGoing()
	{
		var context = CreateContext(
			latestTag: "2.196.75",
			releaseRun: Run(ReleaseRunStatus.InProgress));

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse("an unfinished run has no conclusion to report");
	}

	[Fact]
	public async Task ShouldNotApply_WhenNothingIsKnownAboutTheRun()
	{
		// The local assess path may have no GitHub client, and a tag pushed without a workflow has no
		// run. Neither is evidence of a failure.
		var context = CreateContext(latestTag: "2.196.75", releaseRun: null);

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldNotApply_WhenNoTagIsKnown()
	{
		var context = CreateContext(latestTag: null, releaseRun: null);

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldPass_WhenTheRepositoryPublishesNothing()
	{
		var context = CreateContext(
			latestTag: "2.196.75",
			releaseRun: Run(ReleaseRunStatus.Completed, ReleaseRunConclusion.Failure),
			options: new RepoOptions { IsPackable = false });

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue("a repository that publishes nothing has no release to fail");
	}

	private static IRule Rule() => RuleRegistry.Rules.First(r => r.RuleId == "CI-13");

	private static ReleaseRun Run(
		ReleaseRunStatus status,
		ReleaseRunConclusion? conclusion = null) => new()
		{
			TagRef = "2.196.75",
			RunId = 33757069381,
			Status = status,
			Conclusion = conclusion,
			HtmlUrl = "https://github.com/test-org/Acme.Widget/actions/runs/33757069381",
			StartedAtUtc = new DateTimeOffset(2026, 9, 3, 12, 45, 44, TimeSpan.Zero),
			CompletedAtUtc = status is ReleaseRunStatus.Completed
				? new DateTimeOffset(2026, 9, 3, 12, 46, 26, TimeSpan.Zero)
				: null
		};

	private static RepositoryContext CreateContext(
		string? latestTag,
		ReleaseRun? releaseRun,
		RepoOptions? options = null) => new()
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = options ?? new RepoOptions(),
			FilePaths = ["Acme.Widget/Acme.Widget.csproj"],
			FileContents = new()
			{
				["Acme.Widget/Acme.Widget.csproj"] =
					"<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild></PropertyGroup></Project>"
			},
			LatestTag = latestTag,
			ReleaseRun = releaseRun
		};
}
