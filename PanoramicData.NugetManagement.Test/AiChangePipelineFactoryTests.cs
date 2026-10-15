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

	[Fact]
	public void Slug_NeverEmbedsAPathSeparatorOrSpace()
		=> AiChangePipelineFactory.Slug("rule a/b  c", "x").Should().NotContainAny("/", " ", "\\");
}
