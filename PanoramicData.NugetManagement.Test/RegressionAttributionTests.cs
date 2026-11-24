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
	public void Identify_FindsConsecutiveOursFromHead()
	{
		GitCommit[] commits =
		[
			new("h1", "chore: apply CQ-05 governance remediation"),
			new("h2", "chore: apply LIC-01 governance remediation"),
			new("h3", "feat: unrelated earlier work"),
			new("h4", "chore: apply OLD governance remediation")
		];

		var (count, lastGood) = RegressionAttribution.Identify(commits);

		count.Should().Be(2, "only the two commits at HEAD are consecutively ours");
		lastGood.Should().Be("h2~1", "last-good is the parent of the earliest of our consecutive commits");
	}

	[Fact]
	public void Identify_TipNotOurs_ReturnsNothing()
	{
		GitCommit[] commits =
		[
			new("h1", "feat: someone else's change"),
			new("h2", "chore: apply CQ-05 governance remediation")
		];

		var (count, lastGood) = RegressionAttribution.Identify(commits);

		count.Should().Be(0);
		lastGood.Should().BeNull();
	}

	[Fact]
	public void Identify_AllOurs_RevertsWholeRun()
	{
		GitCommit[] commits =
		[
			new("h1", "chore: apply A governance remediation"),
			new("h2", "chore: apply B governance remediation")
		];

		var (count, lastGood) = RegressionAttribution.Identify(commits);

		count.Should().Be(2);
		lastGood.Should().Be("h2~1");
	}
}
