using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiTestGuide"/>: the guidance that must agree with what the TST rules grade.
/// </summary>
public class AiTestGuideTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData("xunit v3")]
	[InlineData("Microsoft.Testing.Platform")]
	[InlineData("OutputType")]
	[InlineData("AwesomeAssertions")]
	[InlineData("FluentAssertions")]
	[InlineData("failSkips")]
	[InlineData("Microsoft.Testing.Extensions.CodeCoverage")]
	[InlineData("Directory.Packages.props")]
	public void Text_CoversWhatTheTestRulesCheck(string requirement)
		=> AiTestGuide.Text.Should().Contain(requirement);

	[Fact]
	public void Text_TellsTheModelNotToUseTheVsTestAdapter()
		=> AiTestGuide.Text.Should().Contain("xunit.runner.visualstudio");
}
