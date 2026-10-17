using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the red-test requirement on <see cref="IssueAutonomyGate"/>.
/// </summary>
public class IssueAutonomyGateRedTestTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void RequiresRedTest_IsTrueForEveryUnattendedFix()
		=> IssueAutonomyGate.RequiresRedTest.Should().BeTrue(
			"an issue fix is accepted on a test that failed before it and passes after, never on prose");
}
