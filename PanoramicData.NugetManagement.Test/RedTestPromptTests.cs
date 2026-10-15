using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="RedTestPrompt"/>.
/// </summary>
public class RedTestPromptTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly AiChangeRequest Request = new("o", "r", AiChangeKind.IssueFix, "issue-42-a", "Fix #42", 42, true);

	[Fact]
	public void Build_StatesThatTheTestMustFailBeforeAnyFix()
		=> RedTestPrompt.Build(Request, "Parser drops the last row", null)
			.Should().Contain("fail").And.Contain("Parser drops the last row");

	[Fact]
	public void Build_IncludesTheTestGuide()
		=> RedTestPrompt.Build(Request, "x", null).Should().Contain(AiTestGuide.Text);

	[Fact]
	public void Build_FeedsBackWhyThePreviousAttemptWasRejected()
		=> RedTestPrompt.Build(Request, "x", "The test passes on the unfixed code")
			.Should().Contain("The test passes on the unfixed code");

	[Fact]
	public void Build_AsksForTheTestNameAsTheLastLine()
		=> RedTestPrompt.Build(Request, "x", null).Should().Contain("FILTER:");

	[Fact]
	public void Build_OmitsTheRejectionBlockOnTheFirstAttempt()
		=> RedTestPrompt.Build(Request, "x", null).Should().NotContain("previous attempt was rejected");
}
