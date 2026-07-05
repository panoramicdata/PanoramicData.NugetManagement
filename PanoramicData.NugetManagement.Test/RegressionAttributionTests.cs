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
	[InlineData("feat: add a new feature", false)]
	[InlineData("chore: bump versions", false)]
	[InlineData("", false)]
	public void IsGovernanceCommit_MatchesOnlyOurRemediations(string subject, bool expected)
		=> RegressionAttribution.IsGovernanceCommit(subject).Should().Be(expected);

	[Fact]
	public void Identify_FindsConsecutiveOursFromHead()
	{
		var commits = new (string Hash, string Subject)[]
		{
			("h1", "chore: apply CQ-05 governance remediation"),
			("h2", "chore: apply LIC-01 governance remediation"),
			("h3", "feat: unrelated earlier work"),
			("h4", "chore: apply OLD governance remediation")
		};

		var (count, lastGood) = RegressionAttribution.Identify(commits);

		count.Should().Be(2, "only the two commits at HEAD are consecutively ours");
		lastGood.Should().Be("h2~1", "last-good is the parent of the earliest of our consecutive commits");
	}

	[Fact]
	public void Identify_TipNotOurs_ReturnsNothing()
	{
		var commits = new (string Hash, string Subject)[]
		{
			("h1", "feat: someone else's change"),
			("h2", "chore: apply CQ-05 governance remediation")
		};

		var (count, lastGood) = RegressionAttribution.Identify(commits);

		count.Should().Be(0);
		lastGood.Should().BeNull();
	}

	[Fact]
	public void Identify_AllOurs_RevertsWholeRun()
	{
		var commits = new (string Hash, string Subject)[]
		{
			("h1", "chore: apply A governance remediation"),
			("h2", "chore: apply B governance remediation")
		};

		var (count, lastGood) = RegressionAttribution.Identify(commits);

		count.Should().Be(2);
		lastGood.Should().Be("h2~1");
	}
}
