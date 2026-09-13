using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="IssueFixOracle"/>: what stands in for a rule when there is no rule.
/// </summary>
/// <remarks>
/// Every other AI fix in this application is checked by re-running the rule that asked for it, which
/// is free, exact, and the reason a small model can be trusted with a clone at all. An issue has no
/// rule, so the check becomes build-then-test — weaker, and honest about it: passing means "plausible
/// and not broken", never "fixed", and the wording says so because a summary claiming otherwise is
/// how an unreviewed change gets committed.
/// </remarks>
public class IssueFixOracleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static Func<CancellationToken, Task<IssueFixCheckStep>> Step(bool succeeded, string outputText)
		=> _ => Task.FromResult(new IssueFixCheckStep(succeeded, outputText));

	private static Func<CancellationToken, Task<IssueFixCheckStep>> NeverRuns(Action onRun)
		=> _ =>
		{
			onRun();
			return Task.FromResult(new IssueFixCheckStep(true, string.Empty));
		};

	[Fact]
	public async Task CheckAsync_FailsWithTheCompilerOutputWhenTheBuildBreaks()
	{
		var oracle = new IssueFixOracle(
			Step(false, "Documents.cs(12,5): error CS1002: ; expected"),
			Step(true, "all green"));

		var check = await oracle.CheckAsync(TestContext.Current.CancellationToken);

		check.Passed.Should().BeFalse();
		check.Message.Should().Contain("CS1002",
			"the compiler's own words are the correction the model gets, and a paraphrase would "
				+ "lose the line number that makes it actionable");
	}

	[Fact]
	public async Task CheckAsync_DoesNotRunTestsWhenTheBuildFailed()
	{
		var testsRan = false;

		var oracle = new IssueFixOracle(
			Step(false, "error CS1002: ; expected"),
			NeverRuns(() => testsRan = true));

		await oracle.CheckAsync(TestContext.Current.CancellationToken);

		testsRan.Should().BeFalse(
			"a test run against code that does not compile costs minutes and tells the model "
				+ "nothing it was not already about to be told");
	}

	[Fact]
	public async Task CheckAsync_FailsWithTheTestOutputWhenTestsFail()
	{
		var oracle = new IssueFixOracle(
			Step(true, "Build succeeded"),
			Step(false, "DocumentTests.Paging FAILED: expected 3 items, got 1"));

		var check = await oracle.CheckAsync(TestContext.Current.CancellationToken);

		check.Passed.Should().BeFalse();
		check.Message.Should().Contain("DocumentTests.Paging");
	}

	[Fact]
	public async Task CheckAsync_PassesWhenTheCodeBuildsAndTestsAreGreen()
	{
		var oracle = new IssueFixOracle(Step(true, "Build succeeded"), Step(true, "1310 passed"));

		var check = await oracle.CheckAsync(TestContext.Current.CancellationToken);

		check.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CheckAsync_SaysGreenIsNotProofTheIssueIsFixed()
	{
		var oracle = new IssueFixOracle(Step(true, "Build succeeded"), Step(true, "1310 passed"));

		var check = await oracle.CheckAsync(TestContext.Current.CancellationToken);

		check.Message.Should().Contain("not proof",
			"a summary that claimed the issue was fixed is how an unreviewed change derived from a "
				+ "stranger's prose ends up committed on somebody's word");
	}
}
