using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the small decisions <see cref="AiChangePipelineFactory"/> makes on the pipeline's behalf.
/// </summary>
public class AiChangePipelineFactoryTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData("Lib.Test/ParserTests.cs", true)]
	[InlineData("Lib.Tests/ParserTests.cs", true)]
	[InlineData("src/ParserTests.cs", true)]
	[InlineData("Lib/Parser.cs", false)]
	[InlineData("Lib/Contest.cs", false)]
	public void IsTestPath_RecognisesTestFiles(string path, bool expected)
		=> AiChangePipelineFactory.IsTestPath(path).Should().Be(expected);

	[Theory]
	[InlineData("rule CI-15", "a1b2", "rule-ci-15-a1b2")]
	[InlineData("issue 42", "c3d4", "issue-42-c3d4")]
	public void Slug_IsLowerCaseBranchSafeAndUnique(string label, string unique, string expected)
		=> AiChangePipelineFactory.Slug(label, unique).Should().Be(expected);

	[Theory]
	[InlineData("src/Foo.cs", "src/Foo.cs")]
	[InlineData("./src/Foo.cs", "src/Foo.cs")]
	[InlineData("src\\Foo.cs", "src/Foo.cs")]
	[InlineData("/src/Foo.cs", "src/Foo.cs")]
	public void NormalisePath_GivesOneSpellingToEveryWayOfWritingAPath(string path, string expected)
		=> AiChangePipelineFactory.NormalisePath(path).Should().Be(expected);

	[Fact]
	public void IssueFixLimits_AllowTheBriefsPathsHoweverTheyAreSpelled()
	{
		var limits = AiChangePipelineFactory.IssueFixLimits(["src\\Foo.cs", "./src/Bar.cs"], []);

		limits.IsAllowedPath("src/Foo.cs").Should().BeTrue(
			"the toolbox let the model write the file the brief spelled with a backslash, so the gate must accept it too");
		limits.IsAllowedPath("src/Bar.cs").Should().BeTrue();
		limits.IsAllowedPath("src/Other.cs").Should().BeFalse();
	}

	[Fact]
	public void IssueFixLimits_TreatEveryFileInATestFolderAsATestFile()
	{
		var limits = AiChangePipelineFactory.IssueFixLimits(["src/Foo.cs"], ["test/Foo.Specs"]);

		limits.IsAllowedPath("test/Foo.Specs/Fixture.cs").Should().BeTrue(
			"the red-test step may write any new file under the test folder, whatever the project is called");
		limits.IsAllowedPath("test/Foo.Specs/xunit.runner.json").Should().BeTrue();
		limits.IsTestPath("test/Foo.Specs/Fixture.cs").Should().BeTrue("a helper in the test project is not part of the fix's footprint");
		limits.IsTestPath("src/Foo.cs").Should().BeFalse();
	}

	[Fact]
	public void IssueFixLimits_RefuseAFileOutsideBothTheBriefAndTheTestFolders()
		=> AiChangePipelineFactory.IssueFixLimits(["src/Foo.cs"], ["test/Foo.Specs"])
			.IsAllowedPath(".github/workflows/ci.yml").Should().BeFalse();

	[Fact]
	public void IssueFixLimits_CapAtTheUnattendedMaximum()
		=> AiChangePipelineFactory.IssueFixLimits(["a.cs"], []).MaxPaths
			.Should().Be(PanoramicData.NugetManagement.Services.IssueAutonomyGate.MaxUnattendedPaths);

	[Fact]
	public void Shorten_LeavesAShortSingleLineAlone()
		=> AiChangePipelineFactory.Shorten("Parser drops the last row", 60).Should().Be("Parser drops the last row");

	[Fact]
	public void Shorten_CutsALongTextToTheLengthWithAnEllipsis()
	{
		var shortened = AiChangePipelineFactory.Shorten(new string('x', 100), 20);

		shortened.Should().HaveLength(20);
		shortened.Should().EndWith("...");
	}

	[Fact]
	public void Shorten_PutsALineBreakOnOneLine_BecauseATitleCannotHaveOne()
		=> AiChangePipelineFactory.Shorten("first line\r\nsecond line", 60).Should().Be("first line second line");

	[Fact]
	public void Slug_NeverEmbedsAPathSeparatorOrSpace()
		=> AiChangePipelineFactory.Slug("rule a/b  c", "x").Should().NotContainAny("/", " ", "\\");
}
