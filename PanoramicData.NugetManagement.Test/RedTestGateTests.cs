using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="RedTestGate"/>: whether a failing run is a reproduction or just a failure.
/// </summary>
public class RedTestGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void Validate_AFailingAssertionIsAValidRed()
	{
		var verdict = RedTestGate.Validate(new TestRun(false, "Expected result to be 3, but found 2.\n  total: 1\n  failed: 1"));

		verdict.IsValidRed.Should().BeTrue();
	}

	[Fact]
	public void Validate_APassingTestIsNotRed()
	{
		var verdict = RedTestGate.Validate(new TestRun(true, "total: 1\n  failed: 0\n  succeeded: 1"));

		verdict.IsValidRed.Should().BeFalse();
		verdict.Message.Should().Contain("passes", "the model needs to know it reproduced nothing");
	}

	[Fact]
	public void Validate_ACompileErrorIsNotRed()
	{
		var verdict = RedTestGate.Validate(new TestRun(false, "Parser.cs(4,9): error CS0103: The name 'Foo' does not exist\nBuild FAILED."));

		verdict.IsValidRed.Should().BeFalse("a test that does not compile fails for every bug and none");
		verdict.Message.Should().Contain("compile");
	}

	[Fact]
	public void Validate_AFailureAlongsideOtherPassingTestsIsNotRed()
	{
		var verdict = RedTestGate.Validate(new TestRun(false, "Expected 3 but found 2.\n  total: 40\n  failed: 1\n  succeeded: 39"));

		verdict.IsValidRed.Should().BeFalse(
			"a broad filter that also matched thirty-nine unrelated tests proves nothing about the one that failed");
		verdict.Message.Should().Contain("other tests");
	}

	[Fact]
	public void Validate_AFailureWhoseSummaryCannotBeReadIsNotRed()
	{
		var verdict = RedTestGate.Validate(new TestRun(false, "something exploded"));

		verdict.IsValidRed.Should().BeFalse("a red run is accepted on evidence, and there is none to read");
		verdict.Message.Should().Contain("summary");
	}

	[Fact]
	public void Validate_ARunThatMatchedNoTestsIsNotRed()
	{
		var verdict = RedTestGate.Validate(new TestRun(false, "Zero tests ran\n  total: 0"));

		verdict.IsValidRed.Should().BeFalse("nothing ran, so nothing was reproduced");
		verdict.Message.Should().Contain("no tests");
	}
}
