using PanoramicData.NugetManagement.Web.Services.DocComments;

namespace PanoramicData.NugetManagement.Test.DocComments;

/// <summary>Tests for <see cref="DocBuildVerdict"/>: judging the build that follows insertion.</summary>
public class DocBuildVerdictTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string Ok = "\r\nBuild succeeded.\r\n    0 Error(s)\r\n";

	private const string Missing = @"C:\s\F.cs(1,1): warning CS1591: Missing XML comment for publicly visible type or member 'A'" + "\r\n";

	private static string Diagnostic(string code, string severity = "warning")
		=> $@"C:\s\F.cs(1,1): {severity} {code}: message" + "\r\n";

	[Fact]
	public void AQuietBuildIsComplete()
	{
		var verdict = DocBuildVerdict.Judge(Ok, 5);

		verdict.Passed.Should().BeTrue();
		verdict.Complete.Should().BeTrue();
	}

	[Fact]
	public void RemainingMissingCommentsPassButAreNotComplete()
	{
		var verdict = DocBuildVerdict.Judge(Missing + Ok, 5);

		verdict.Passed.Should().BeTrue();
		verdict.Complete.Should().BeFalse();
		verdict.RemainingMissing.Should().Be(1);
	}

	[Fact]
	public void ADocumentationErrorFailsEvenWhenNothingIsMissing()
		=> DocBuildVerdict.Judge(Diagnostic("CS1584", "error") + "Build FAILED.\r\n", 5).Passed.Should().BeFalse();

	[Fact]
	public void ANewDocumentationWarningFails()
	{
		var verdict = DocBuildVerdict.Judge(Diagnostic("CS1587") + Ok, 5);

		verdict.Passed.Should().BeFalse();
		verdict.Reason.Should().Contain("CS1587");
	}

	[Fact]
	public void AnyCompilerErrorFails()
		=> DocBuildVerdict.Judge(Diagnostic("CS0103", "error") + Ok, 5).Passed.Should().BeFalse();

	[Fact]
	public void OutputWithNoBuildResultIsNotProof()
		=> DocBuildVerdict.Judge(string.Empty, 5).Passed.Should().BeFalse();

	[Fact]
	public void NoProgressFails()
		=> DocBuildVerdict.Judge(Missing + Ok, 1).Passed.Should().BeFalse();
}
