using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DashboardAiChangeTests.PassedGivenBaseline"/>: a suite is green if nothing new broke.
/// </summary>
public class KnownFailureGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static IReadOnlySet<string> Set(params string[] names) => names.ToHashSet(StringComparer.Ordinal);

	[Fact]
	public void AGreenRun_Passes()
		=> DashboardAiChangeTests.PassedGivenBaseline(true, Set(), Set()).Should().BeTrue();

	[Fact]
	public void FailuresThatWereAlreadyThereBeforeTheChange_StillPass()
		=> DashboardAiChangeTests.PassedGivenBaseline(false, Set("Credentialed.Test"), Set("Credentialed.Test", "Other.Test"))
			.Should().BeTrue("they fail here for a reason that has nothing to do with the change, as they did before it");

	[Fact]
	public void ANewFailureAlongsideKnownOnes_Fails()
		=> DashboardAiChangeTests.PassedGivenBaseline(false, Set("Credentialed.Test", "Broken.ByTheChange"), Set("Credentialed.Test"))
			.Should().BeFalse();

	[Fact]
	public void WithNoBaseline_AnyFailureFails()
		=> DashboardAiChangeTests.PassedGivenBaseline(false, Set("Some.Test"), Set()).Should().BeFalse();

	[Fact]
	public void ARedRunWithNoTestFailuresAtAll_Fails()
		=> DashboardAiChangeTests.PassedGivenBaseline(false, Set(), Set("Credentialed.Test")).Should().BeFalse(
			"a run that failed without naming a failing test, such as a build error, is not 'only known failures'");
}
