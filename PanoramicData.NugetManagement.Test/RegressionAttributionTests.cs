using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="RegressionAttribution"/> — deciding whether a build regression is ours to
/// roll back, and how far back to revert.
/// </summary>
public class RegressionAttributionTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData("chore: apply CQ-05 governance remediation", true)]
	[InlineData("chore: apply CodeQuality governance remediations", true)]
	[InlineData("chore: apply governance remediations", true)]
	[InlineData("revert: auto-rollback governance remediation (build regression)", false)] // our revert must NOT be re-reverted
	[InlineData("NuGet governance remediation for panoramicdata/PanoramicData.NugetManagement", true)]
	[InlineData("Revert \"NuGet governance remediation for panoramicdata/PanoramicData.NugetManagement\"", false)]
	[InlineData("feat: add a new feature", false)]
	[InlineData("chore: bump versions", false)]
	[InlineData("", false)]
	public void IsGovernanceCommit_MatchesOnlyOurRemediations(string subject, bool expected)
		=> RegressionAttribution.IsGovernanceCommit(subject).Should().Be(expected);

	[Fact]
	public void IsGovernanceCommit_TrailerDecidesWhateverTheSubjectSays()
		=> RegressionAttribution.IsGovernanceCommit("Tidy up", "CQ-05").Should().BeTrue();

	[Theory]
	[InlineData(0, "h4")]
	[InlineData(1, "h3")]
	[InlineData(2, "h2")]
	[InlineData(3, "h1")]
	public async Task FindFirstBreakingCommitAsync_NamesTheSingleCommitThatBrokeTheBuild(int goodCount, string expected)
	{
		// Newest first. The first `goodCount` commits from the oldest end still build.
		GitCommit[] commits =
		[
			new("h1", "chore: apply A governance remediations"),
			new("h2", "chore: apply B governance remediations"),
			new("h3", "chore: apply C governance remediations"),
			new("h4", "chore: apply D governance remediations")
		];
		var oldestFirst = commits.Reverse().Select(c => c.Hash).ToList();
		var built = new List<string>();

		var culprit = await RegressionAttribution.FindFirstBreakingCommitAsync(
			commits,
			hash =>
			{
				built.Add(hash);
				return Task.FromResult(oldestFirst.IndexOf(hash) < goodCount);
			});

		// goodCount 0 means the oldest commit (h4) is the culprit, and so on up to h1 at goodCount 3.
		culprit.Hash.Should().Be(expected);
		built.Should().NotContain("h1", "HEAD is already known to fail, so it is never rebuilt");
	}

	[Fact]
	public void Identify_CollectsOursAcrossAHumanCommit()
	{
		GitCommit[] commits =
		[
			new("h1", "NuGet governance remediation for o/r"),
			new("h2", "fix: a human commit on top of ours"),
			new("h3", "NuGet governance remediation for o/r"),
			new("h4", "feat: unrelated earlier work")
		];

		var attribution = RegressionAttribution.Identify(commits);

		attribution.Ours.Select(c => c.Hash).Should().Equal("h1", "h3");
		attribution.LastGoodRef.Should().Be("h3~1", "the control is the parent of the earliest of ours");
		attribution.LastGoodVerified.Should().BeFalse();
	}

	[Fact]
	public void Identify_HumanCommitOnTop_IsNoLongerNotOurs()
	{
		GitCommit[] commits =
		[
			new("h1", "feat: someone else's change"),
			new("h2", "NuGet governance remediation for o/r")
		];

		RegressionAttribution.Identify(commits).Ours.Select(c => c.Hash).Should().Equal("h2");
	}

	[Fact]
	public void Identify_NoneOurs_ReturnsNothing()
	{
		GitCommit[] commits = [new("h1", "feat: a"), new("h2", "feat: b")];

		var attribution = RegressionAttribution.Identify(commits);

		attribution.Ours.Should().BeEmpty();
		attribution.LastGoodRef.Should().BeNull();
	}

	[Fact]
	public void Identify_StopsAtTheLastVerifiedCommit_AndUsesItAsTheControl()
	{
		GitCommit[] commits =
		[
			new("h1", "NuGet governance remediation for o/r"),
			new("h2", "fix: human"),
			new("h3", "NuGet governance remediation for o/r"),
			new("h4", "NuGet governance remediation for o/r")
		];

		var attribution = RegressionAttribution.Identify(commits, "h3");

		attribution.Ours.Select(c => c.Hash).Should().Equal("h1");
		attribution.LastGoodRef.Should().Be("h3");
		attribution.LastGoodVerified.Should().BeTrue("a commit seen building green needs no control build");
	}

	[Fact]
	public void Identify_VerifiedHashOutsideTheWindow_FallsBackToTheControlBuild()
	{
		GitCommit[] commits = [new("h1", "NuGet governance remediation for o/r")];

		var attribution = RegressionAttribution.Identify(commits, "gone");

		attribution.LastGoodRef.Should().Be("h1~1");
		attribution.LastGoodVerified.Should().BeFalse();
	}

	[Fact]
	public void Identify_VerifiedCommitAtTheTip_LeavesNothingToBlame()
	{
		GitCommit[] commits = [new("h1", "NuGet governance remediation for o/r")];

		RegressionAttribution.Identify(commits, "h1").Ours.Should().BeEmpty();
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public async Task NameCulpritAsync_BlamesUsOnlyWhenTheCommitBeforeTheCulpritBuilds(bool parentFails, bool expectedOurs)
	{
		// Newest first: h1 and h3 are ours, h2 is a human commit between them. h3 builds, h1 does not.
		var attribution = new Attribution([new("h1", "ours"), new("h3", "ours")], "h3~1", false, true);

		var (culprit, isOurs) = await RegressionAttribution.NameCulpritAsync(
			attribution,
			reference => Task.FromResult(reference switch
			{
				"h3" => true,
				"h1~1" => !parentFails,
				_ => false
			}));

		culprit.Hash.Should().Be("h1");
		isOurs.Should().Be(expectedOurs);
	}

	[Fact]
	public async Task NameCulpritAsync_WhenTheEarliestOfOursFails_NeedsNoParentBuild()
	{
		var attribution = new Attribution([new("h1", "ours")], "h1~1", false, true);
		var asked = new List<string>();

		var (culprit, isOurs) = await RegressionAttribution.NameCulpritAsync(
			attribution,
			reference =>
			{
				asked.Add(reference);
				return Task.FromResult(false);
			});

		culprit.Hash.Should().Be("h1");
		isOurs.Should().BeTrue("the control build already proved its parent");
		asked.Should().BeEmpty();
	}

	[Fact]
	public async Task NameCulpritAsync_WhenTheNewestOfOursBuildsUnderAHumanCommit_BlamesNobodyOfOurs()
	{
		var attribution = new Attribution([new("h2", "ours")], "h2~1", false, false);

		var (culprit, isOurs) = await RegressionAttribution.NameCulpritAsync(attribution, reference => Task.FromResult(reference == "h2"));

		culprit.Hash.Should().Be("h2");
		isOurs.Should().BeFalse("the human commit above it broke the build, not our commit");
	}
}
