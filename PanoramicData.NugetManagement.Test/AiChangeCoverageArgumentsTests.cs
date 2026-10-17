using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the arguments of a coverage-collecting test run.
/// </summary>
public class AiChangeCoverageArgumentsTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void BuildCoverageArguments_AsksForCoberturaAtTheGivenPath()
	{
		var args = LocalRepoService.BuildCoverageArguments("Lib.Test/Lib.Test.csproj", @"C:\tmp\c.xml", null);

		args.Should().ContainInOrder("test", "Lib.Test/Lib.Test.csproj");
		args.Should().Contain("--coverage");
		args.Should().ContainInOrder("--coverage-output-format", "cobertura");
		args.Should().ContainInOrder("--coverage-output", @"C:\tmp\c.xml");
	}

	[Fact]
	public void BuildCoverageArguments_AddsAFilterWhenGiven()
		=> LocalRepoService.BuildCoverageArguments("p.csproj", "c.xml", "FullyQualifiedName~Parser")
			.Should().ContainInOrder("--filter", "FullyQualifiedName~Parser");

	[Fact]
	public void BuildCoverageArguments_OmitsTheFilterWhenNoneIsGiven()
		=> LocalRepoService.BuildCoverageArguments("p.csproj", "c.xml", null)
			.Should().NotContain("--filter");
}
