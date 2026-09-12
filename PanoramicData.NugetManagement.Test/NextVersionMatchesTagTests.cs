using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for VER-05, which reports work that is committed but not released: what nbgv would version
/// the working tree as, against the newest tag it actually carries.
/// </summary>
/// <remarks>
/// Informational by design — every repository with a commit since its last release is in this state,
/// and being in it is normal. What makes it worth saying is that nothing else does: CI-11 compares a
/// tag with nuget.org and is silent about commits that were never tagged at all.
/// </remarks>
public class NextVersionMatchesTagTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void ShouldBeInformational_NotAnError()
	{
		// The whole point of the rule: an unreleased commit is a fact about the repository, not a
		// failure of it, and a dashboard that reported it in red would be wrong about every repository
		// anyone is working in.
		Rule().Severity.Should().Be(AssessmentSeverity.Info);
	}

	[Fact]
	public async Task ShouldReport_WhenTheWorkingTreeIsAheadOfTheNewestTag()
	{
		// Uk.Parliament, 2026-09-12: newest tag 10.1.12, two commits past it, so nbgv would version
		// the next release 10.1.14.
		var context = CreateContext(latestTag: "10.1.12", nextVersion: "10.1.14");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("10.1.14").And.Contain("10.1.12");
	}

	[Fact]
	public async Task ShouldPass_WhenTheNewestTagIsTheWorkingTreeVersion()
	{
		// Divoom.Api, 2026-09-12: HEAD is the tagged commit, so nbgv computes the tag itself.
		var context = CreateContext(latestTag: "1.0.43", nextVersion: "1.0.43");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue("everything committed here has been released");
	}

	[Fact]
	public async Task ShouldPass_WhenTheNewestTagIsAheadOfTheWorkingTree()
	{
		// A clone that has fetched tags but not the commits behind them. Nothing here is unreleased.
		var context = CreateContext(latestTag: "1.0.50", nextVersion: "1.0.43");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldNotApply_WhenTheCloneIsNotKnownToMatchOrigin()
	{
		// The finding this rule makes is only true of a clone that is up to date. Divoom.Api and
		// Uk.Parliament were both three or more releases behind origin when this was written, and
		// their local commits past the local tag had long since been released by someone else —
		// reported as unreleased work, that is an invention.
		var context = CreateContext(
			latestTag: "10.1.12",
			nextVersion: "10.1.14",
			confirmedInSyncWithOrigin: false);

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue();
		result.Message.Should().Contain("Sync");
	}

	[Fact]
	public async Task ShouldNotApply_WhenNbgvCouldNotBeAsked()
	{
		// No nbgv on the machine, or it failed: no number is better than a guessed one.
		var context = CreateContext(latestTag: "10.1.12", nextVersion: null);

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldNotApply_WhenNoTagIsKnown()
	{
		var context = CreateContext(latestTag: null, nextVersion: "1.0.3");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse("a repository with no release yet has nothing to compare against");
	}

	[Fact]
	public async Task ShouldNotApply_WhenTheCloneIsNotOnItsDefaultBranch()
	{
		// A feature branch's height counts unmerged commits, which are not waiting to be released.
		var context = CreateContext(
			latestTag: "10.1.12",
			nextVersion: "10.1.14",
			currentBranch: "feature/something");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldPass_WhenTheRepositoryPublishesNothing()
	{
		var context = CreateContext(
			latestTag: "10.1.12",
			nextVersion: "10.1.14",
			options: new RepoOptions { IsPackable = false });

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldToleratePrefixedTags()
	{
		var context = CreateContext(latestTag: "v1.0.43", nextVersion: "1.0.43");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue("a leading v is a tag convention, not a different version");
	}

	[Fact]
	public async Task ShouldNotApply_WhenTheTagIsNotAVersion()
	{
		var context = CreateContext(latestTag: "Portal_v1.0.131", nextVersion: "1.3.7");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task ShouldSayHowToReleaseTheWork()
	{
		var context = CreateContext(latestTag: "10.1.12", nextVersion: "10.1.14");

		var result = await Rule().EvaluateAsync(context, CancellationToken.None);

		result.Advisory.Should().NotBeNull();
		result.Advisory!.Detail.Should().Contain("Publish.ps1");
	}

	private static IRule Rule() => RuleRegistry.Rules.First(r => r.RuleId == "VER-05");

	private static RepositoryContext CreateContext(
		string? latestTag,
		string? nextVersion,
		RepoOptions? options = null,
		bool confirmedInSyncWithOrigin = true,
		string currentBranch = "main") => new()
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = currentBranch,
			Options = options ?? new RepoOptions(),
			FilePaths = ["Acme.Widget/Acme.Widget.csproj"],
			FileContents = new()
			{
				["Acme.Widget/Acme.Widget.csproj"] =
					"<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild></PropertyGroup></Project>"
			},
			LatestTag = latestTag,
			NextVersion = nextVersion,
			IsConfirmedInSyncWithOrigin = confirmedInSyncWithOrigin
		};
}
