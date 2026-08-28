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

	private static IRule Rule() => RuleRegistry.Rules.First(r => r.RuleId == "CI-11");

	private static RepositoryContext CreateContext(
		string? latestTag,
		string? latestPublishedVersion,
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
			LatestPublishedVersion = latestPublishedVersion
		};
}
