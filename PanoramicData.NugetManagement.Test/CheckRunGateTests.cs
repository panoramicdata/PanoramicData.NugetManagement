using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="CheckRunGate"/>: what a pull request's check runs add up to.
/// </summary>
public class CheckRunGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static CheckRunState Run(CheckRunConclusion conclusion) => new("build", conclusion);

	[Fact]
	public void Evaluate_NoRuns_IsNoChecks()
		=> CheckRunGate.Evaluate([]).Should().Be(CheckGateResult.NoChecks);

	[Fact]
	public void Evaluate_AllPassed_IsPassed()
		=> CheckRunGate.Evaluate([Run(CheckRunConclusion.Passed), Run(CheckRunConclusion.Passed)])
			.Should().Be(CheckGateResult.Passed);

	[Fact]
	public void Evaluate_SomethingStillRunning_IsPending()
		=> CheckRunGate.Evaluate([Run(CheckRunConclusion.Passed), Run(CheckRunConclusion.Pending)])
			.Should().Be(CheckGateResult.Pending);

	[Fact]
	public void Evaluate_AFailureWinsOverPending()
		=> CheckRunGate.Evaluate([Run(CheckRunConclusion.Pending), Run(CheckRunConclusion.Failed)])
			.Should().Be(CheckGateResult.Failed,
				"a failed check cannot be undone by a slower one finishing, so there is no reason to keep waiting");
}
