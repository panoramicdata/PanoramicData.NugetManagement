using Microsoft.Extensions.Time.Testing;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for CI-11, which catches a release that was tagged but never reached nuget.org. The estate
/// carried nine of these at once, some for months, because a pushed tag was reported as success and
/// nothing ever compared it with what was published.
/// </summary>
public class PublishedVersionMatchesTagTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task ShouldFail_WhenTheTagIsAheadOfWhatWasPublished()
	{
		var context = CreateContext(latestTag: "3.264.11", latestPublishedVersion: "3.240.3");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("3.264.11").And.Contain("3.240.3");
	}

	[Fact]
	public async Task ShouldOrderVersionsNumerically_NotAsText()
	{
		// 3.264.11 is ahead of 3.240.3, but sorts before it as text — the comparison this rule exists
		// to make is exactly the one a string compare gets wrong.
		var context = CreateContext(latestTag: "3.264.11", latestPublishedVersion: "3.240.3");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse("3.264.11 is a later release than 3.240.3");
	}

	[Fact]
	public async Task ShouldPass_WhenPublishedMatchesTheTag()
	{
		var context = CreateContext(latestTag: "1.0.55", latestPublishedVersion: "1.0.55");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldPass_WhenPublishedIsAheadOfTheNewestLocalTag()
	{
		// A clone that has not fetched recently knows an older tag than the estate has published.
		// That is a stale clone, not a failed release.
		var context = CreateContext(latestTag: "1.0.55", latestPublishedVersion: "1.0.60");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldFail_WhenTaggedButNeverPublished()
	{
		var context = CreateContext(latestTag: "1.0.0", latestPublishedVersion: null);

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse("a tagged repository with nothing on nuget.org is the worst case, not an unknown");
	}

	[Fact]
	public async Task ShouldNotApply_WhenNoTagIsKnown()
	{
		// Not cloned, or never tagged: the comparison cannot be made, and saying so beats passing.
		var context = CreateContext(latestTag: null, latestPublishedVersion: "1.0.0");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldNotApply_WhenTheRepositoryPublishesNothing()
	{
		var context = CreateContext(
			latestTag: "1.0.0",
			latestPublishedVersion: null,
			options: new RepoOptions { IsPackable = false });

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldToleratePrefixedTags()
	{
		var context = CreateContext(latestTag: "v1.0.55", latestPublishedVersion: "1.0.55");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue("a leading v is a tag convention, not a different version");
	}

	[Fact]
	public async Task ShouldNotApply_WhenTheTagIsNotAVersion()
	{
		var context = CreateContext(latestTag: "release-candidate", latestPublishedVersion: "1.0.0");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldPass_WhileTheReleaseRunIsStillGoing()
	{
		// The gap between a tag being pushed and the package appearing is where this rule used to
		// report a failure that had not happened yet.
		var context = CreateContext(
			latestTag: "2.196.75",
			latestPublishedVersion: "2.196.66",
			releaseRun: Run(ReleaseRunStatus.InProgress));

		var result = await Rule(_now).EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue("the release has not finished, so nothing has failed");
		result.Message.Should().Contain("2.196.75");
	}

	[Fact]
	public async Task ShouldPass_WhileTheFirstEverReleaseIsStillGoing()
	{
		var context = CreateContext(
			latestTag: "1.0.0",
			latestPublishedVersion: null,
			releaseRun: Run(ReleaseRunStatus.Queued));

		var result = await Rule(_now).EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue("a first release in flight is not a first release that failed");
	}

	[Fact]
	public async Task ShouldPass_WhenTheRunSucceededAndTheVersionIndexHasNotCaughtUp()
	{
		// HaloPsa.Api, 2026-09-03: the publish job succeeded and the package was pushed, but the
		// assessment read nuget.org's version index a minute later and still saw the old version.
		var context = CreateContext(
			latestTag: "2.196.75",
			latestPublishedVersion: "2.196.66",
			releaseRun: Run(
				ReleaseRunStatus.Completed,
				ReleaseRunConclusion.Success,
				completedAt: _now.AddMinutes(-1)));

		var result = await Rule(_now).EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue("the release succeeded a minute ago; nuget.org is still indexing");
	}

	[Fact]
	public async Task ShouldFail_WhenTheRunSucceededLongAgoAndNothingEverLanded()
	{
		// A publish step that reports success without producing a package is the failure this rule
		// exists for. The grace period must not swallow it.
		var context = CreateContext(
			latestTag: "2.196.75",
			latestPublishedVersion: "2.196.66",
			releaseRun: Run(
				ReleaseRunStatus.Completed,
				ReleaseRunConclusion.Success,
				completedAt: _now.AddHours(-3)));

		var result = await Rule(_now).EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("2.196.75");
	}

	[Fact]
	public async Task ShouldNotApply_WhenTheReleaseRunFailed()
	{
		// CI-13 owns a failed run. Reporting it here as well would bill one broken release as two
		// findings, and the version comparison is the less useful of the two.
		var context = CreateContext(
			latestTag: "2.196.75",
			latestPublishedVersion: "2.196.66",
			releaseRun: Run(
				ReleaseRunStatus.Completed,
				ReleaseRunConclusion.Failure,
				completedAt: _now.AddMinutes(-5)));

		var result = await Rule(_now).EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue("a failed run is CI-13's finding, not this rule's");
		result.Message.Should().Contain("CI-13");
	}

	[Fact]
	public async Task ShouldFail_WhenNothingIsKnownAboutTheRun()
	{
		// No GitHub client on the local assess path, or no run found for the tag: the rule has no
		// excuse to accept, so it reports as it always did.
		var context = CreateContext(latestTag: "2.196.75", latestPublishedVersion: "2.196.66");

		var result = await Rule(_now).EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
	}

	private readonly DateTimeOffset _now = new(2026, 9, 3, 13, 0, 0, TimeSpan.Zero);

	private static IRule Rule() => RuleRegistry.Rules.First(r => r.RuleId == "CI-11");

	private static PublishedVersionMatchesTagRule Rule(DateTimeOffset now)
		=> new(new FakeTimeProvider(now));

	private ReleaseRun Run(
		ReleaseRunStatus status,
		ReleaseRunConclusion? conclusion = null,
		DateTimeOffset? completedAt = null) => new()
		{
			TagRef = "2.196.75",
			RunId = 33757069381,
			Status = status,
			Conclusion = conclusion,
			HtmlUrl = "https://github.com/test-org/Acme.Widget/actions/runs/33757069381",
			StartedAtUtc = _now.AddMinutes(-10),
			CompletedAtUtc = completedAt
		};

	private static RepositoryContext CreateContext(
		string? latestTag,
		string? latestPublishedVersion,
		RepoOptions? options = null,
		ReleaseRun? releaseRun = null) => new()
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
			LatestPublishedVersion = latestPublishedVersion,
			ReleaseRun = releaseRun
		};
}
