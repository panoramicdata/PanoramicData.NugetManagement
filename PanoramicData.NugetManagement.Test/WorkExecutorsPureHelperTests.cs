using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the pure decisions an AI fix makes about what happened, kept apart from the session that
/// makes them so they can be checked without a model.
/// </summary>
public class WorkExecutorsPureHelperTests
{
	[Theory]
	[InlineData(true, null, false, "applied")]
	[InlineData(true, "1.0% to 5.0%", true, "applied")]
	[InlineData(false, null, true, "cancelled")]
	[InlineData(false, "1.0% to 5.0%", true, "cancelled")]
	[InlineData(false, "1.0% to 5.0%", false, "kept")]
	[InlineData(false, null, false, "reverted")]
	public void SessionVerdict_NamesWhatBecameOfTheSession(bool succeeded, string? kept, bool cancelled, string expected)
		=> WorkExecutors.SessionVerdict(succeeded, kept, cancelled).Should().Be(expected);

	[Theory]
	[InlineData(1, "✅ Fixed REPO-01")]
	[InlineData(3, "✅ Fixed REPO-01")]
	[InlineData(0, "⚠️ Could not fix REPO-01")]
	public void DescribeFix_SaysWhetherAnythingWasApplied(int applied, string expected)
		=> WorkExecutors.DescribeFix("REPO-01", applied).Should().Be(expected);

	[Fact]
	public void DescribeMeasurement_ExplainsWhyCoverageCouldNotBeMeasured()
	{
		var run = new CoverageRun(false, null, "no test project", string.Empty);

		WorkExecutors.DescribeMeasurement(run, testsFine: false)
			.Should().Contain("could not be measured").And.Contain("no test project");
	}
}
