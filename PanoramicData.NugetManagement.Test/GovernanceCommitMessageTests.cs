using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>Tests for <see cref="GovernanceCommitMessage"/>: the body, the trailer and the diff parsing.</summary>
public class GovernanceCommitMessageTests
{
	[Fact]
	public void Compose_NamesRulesFilesAndPackageMoves_AndEndsWithTheTrailer()
	{
		var message = GovernanceCommitMessage.Compose(
			GovernanceCommitMessage.Subject("panoramicdata/Demo.Api"),
			["LIC-01", "CQ-05"],
			["Directory.Packages.props", "SECURITY.md"],
			["Serilog 3.0.0 -> 4.0.0"]);

		var lines = message.Split('\n');
		lines[0].Should().Be("NuGet governance remediation for panoramicdata/Demo.Api");
		message.Should().Contain("Rules: CQ-05, LIC-01");
		message.Should().Contain("- Directory.Packages.props").And.Contain("- SECURITY.md");
		message.Should().Contain("- Serilog 3.0.0 -> 4.0.0");
		lines[^1].Should().Be("Governance-Remediation: CQ-05, LIC-01");
		lines[^2].Should().BeEmpty("the trailer is its own paragraph, which is what git reads trailers from");
	}

	[Fact]
	public void Compose_WithNoRuleIds_StillWritesTheTrailer()
		=> GovernanceCommitMessage.Compose("s", [], [], []).Should().EndWith("Governance-Remediation: unspecified");

	[Fact]
	public void Compose_DropsRuleIdsThatCouldInjectAnotherTrailer()
		=> GovernanceCommitMessage.Compose("s", ["CQ-05\nSigned-off-by: x", "LIC-01"], [], [])
			.Should().EndWith("Governance-Remediation: LIC-01");

	[Fact]
	public void Compose_ListsAtMostFiftyFiles()
	{
		var files = Enumerable.Range(0, 60).Select(i => $"f{i}.cs").ToList();

		var message = GovernanceCommitMessage.Compose("s", ["CQ-05"], files, []);

		message.Should().Contain("Files (60):").And.Contain("- and 10 more").And.NotContain("- f55.cs");
	}

	[Theory]
	[InlineData("CQ-05, LIC-01", "CQ-05,LIC-01")]
	[InlineData("unspecified", "unspecified")]
	public void ReadRuleIds_SplitsWhatComposeWrote(string trailer, string expected)
		=> string.Join(',', GovernanceCommitMessage.ReadRuleIds(trailer)).Should().Be(expected);

	[Fact]
	public void PackageMoves_ReportsChangedAndAddedPackagesOnly()
	{
		var diff = string.Join('\n',
			"diff --git a/Directory.Packages.props b/Directory.Packages.props",
			"--- a/Directory.Packages.props",
			"+++ b/Directory.Packages.props",
			"@@ -3,3 +3,4 @@",
			"-    <PackageVersion Include=\"Serilog\" Version=\"3.0.0\" />",
			"+    <PackageVersion Include=\"Serilog\" Version=\"4.0.0\" />",
			"-    <PackageVersion Include=\"Same\" Version=\"1.0.0\" />",
			"+    <PackageVersion Include=\"Same\" Version=\"1.0.0\" />",
			"+    <PackageVersion Include=\"Brand.New\" Version=\"2.1.0\" />");

		GovernanceCommitMessage.PackageMoves(diff).Should().Equal(
			"Brand.New 2.1.0 (added)",
			"Serilog 3.0.0 -> 4.0.0");
	}

	[Fact]
	public void PackageMoves_IgnoresLinesThatAreNotPackages()
		=> GovernanceCommitMessage.PackageMoves("-<LangVersion>12</LangVersion>\n+<LangVersion>13</LangVersion>").Should().BeEmpty();

	[Theory]
	[InlineData("NuGet governance remediation for panoramicdata/Demo.Api", true)]
	[InlineData("Revert \"NuGet governance remediation for panoramicdata/Demo.Api\"", false)]
	[InlineData("Merge NuGet governance remediation for x", false)]
	[InlineData(null, false)]
	public void IsRemediationSubject_MatchesOnlyOurOwnSubjectAtTheStart(string? subject, bool expected)
		=> GovernanceCommitMessage.IsRemediationSubject(subject).Should().Be(expected);
}
