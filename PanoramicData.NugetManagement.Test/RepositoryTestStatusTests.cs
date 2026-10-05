using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the per-repository test status: that it is remembered, that it is thrown away with the build
/// result rather than outliving it, and that it survives the cache.
/// </summary>
public class RepositoryTestStatusTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			Directory.Delete(_directory, recursive: true);
		}
		catch (IOException)
		{
			// A locked temp file must not fail the test that produced it.
		}

		GC.SuppressFinalize(this);
	}

	private static RepositoryDashboardRow Row() => new()
	{
		Organization = "panoramicdata",
		RepositoryFullName = "panoramicdata/Sample"
	};

	[Fact]
	public void ANewRow_HasNeverBeenTested()
	{
		var row = Row();

		row.LastTestState.Should().BeNull("not known is not the same as passed");
		row.LastTestedAtUtc.Should().BeNull();
		row.HasVerificationResults.Should().BeFalse();
	}

	[Theory]
	[InlineData(RepositoryTestState.Passed)]
	[InlineData(RepositoryTestState.Failed)]
	public void RememberTestResult_RecordsTheOutcomeAndWhen(RepositoryTestState state)
	{
		var row = Row();
		var at = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

		row.RememberTestResult(state, at);

		row.LastTestState.Should().Be(state);
		row.LastTestedAtUtc.Should().Be(at);
		row.HasVerificationResults.Should().BeTrue();
	}

	[Fact]
	public void ForgetVerificationResults_ThrowsAwayTheBuildAndTheTestResultTogether()
	{
		var row = Row();
		row.LastBuildState = RepositoryBuildState.Succeeded;
		row.LastBuiltAtUtc = DateTimeOffset.UtcNow;
		row.RememberTestResult(RepositoryTestState.Passed, DateTimeOffset.UtcNow);

		row.ForgetVerificationResults();

		row.LastBuildState.Should().BeNull();
		row.LastBuiltAtUtc.Should().BeNull();
		row.LastTestState.Should().BeNull("a test result describes a working tree exactly as a build does");
		row.LastTestedAtUtc.Should().BeNull();
		row.HasVerificationResults.Should().BeFalse();
	}

	[Fact]
	public void AnUnbuiltRowThatHasBeenTested_StillCountsAsHavingResultsToForget()
	{
		// The executor skips the cache write when there is nothing to forget. A row with only a test
		// result must not be mistaken for one with nothing, or its stale result would survive a fix.
		var row = Row();
		row.RememberTestResult(RepositoryTestState.Failed, DateTimeOffset.UtcNow);

		row.HasVerificationResults.Should().BeTrue();
	}

	[Fact]
	public void TheTestResult_SurvivesTheCache()
	{
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, "dashboard-cache.json");
		var at = new DateTimeOffset(2026, 10, 4, 12, 30, 0, TimeSpan.Zero);

		var row = Row();
		row.RememberTestResult(RepositoryTestState.Failed, at);

		new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, path).Update([row]);

		var reloaded = new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, path)
			.GetCachedRows();

		reloaded.Should().NotBeNull().And.ContainSingle();
		reloaded![0].LastTestState.Should().Be(RepositoryTestState.Failed);
		reloaded[0].LastTestedAtUtc.Should().Be(at);
	}

	[Fact]
	public void ARowWrittenBeforeTheseFieldsExisted_ReadsBackAsNotKnown()
	{
		// A cache file from an earlier build has no test fields at all. It must load, with the result
		// not known, rather than failing or inventing a pass.
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, "dashboard-cache.json");

		var old = Row();
		old.LastBuildState = RepositoryBuildState.Succeeded;
		old.LastBuiltAtUtc = DateTimeOffset.UtcNow;

		new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, path).Update([old]);

		var json = File.ReadAllText(path);
		json.Should().NotContainEquivalentOf("TestState",
			"nulls are not written, so a row never tested looks exactly like a file from an earlier build");

		var reloaded = new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, path)
			.GetCachedRows();

		reloaded![0].LastTestState.Should().BeNull();
		reloaded[0].LastBuildState.Should().Be(RepositoryBuildState.Succeeded);
	}
}
