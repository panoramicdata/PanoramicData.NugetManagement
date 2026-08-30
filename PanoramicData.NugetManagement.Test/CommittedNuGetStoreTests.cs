using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Reads the real, committed <c>nuget-versions.json</c> and <c>nuget-floors.json</c> from the
/// repository root.
/// </summary>
/// <remarks>
/// <para>
/// Nothing else in this suite does. <see cref="RuleAssessmentIsolation"/> substitutes in-memory
/// stores process-wide before any test runs, and the self-assessment tests filter PKG-05/06/07 out
/// of their assertions, so a hand-edit that dropped a comma — or a serializer change that renamed
/// <c>published</c> — would be caught by the stores' own catch blocks, leave every package unknown,
/// turn all three rules green estate-wide, and leave the suite fully passing. The feature would be
/// dead and every signal would say it was healthy.
/// </para>
/// <para>
/// These tests therefore construct stores explicitly against the real paths. They do not touch
/// <c>Default</c>, do not write, and so do not weaken the module initializer that keeps this binary
/// off those files: that initializer exists because tests writing them made the suite
/// order-dependent, and reading is not writing.
/// </para>
/// </remarks>
public class CommittedNuGetStoreTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void TheCommittedVersionCacheShouldParse()
	{
		var path = RepositoryRootFile.Resolve(NuGetVersionCache.FileName);
		path.Should().NotBeNull("the repository root is found by walking up to the .slnx, as production does");
		File.Exists(path).Should().BeTrue("the seeded cache is committed");

		var cache = new NuGetVersionCache(path);

		cache.LoadFailed.Should().BeFalse(
			"a cache that will not parse silently disables the upstream half of the version gate: " + cache.LoadFailure);
		cache.PackageIds.Should().NotBeEmpty("an empty cache is every package unknown, which is the same failure");
	}

	[Fact]
	public void EveryCommittedSnapshotShouldCarryTheFieldsTheRulesReadFromIt()
	{
		var cache = new NuGetVersionCache(RepositoryRootFile.Resolve(NuGetVersionCache.FileName));

		foreach (var packageId in cache.PackageIds)
		{
			cache.TryGet(packageId, out var snapshot).Should().BeTrue();
			snapshot!.LatestVersion.Should().NotBeNullOrWhiteSpace($"{packageId} needs a version to compare against");
			snapshot.Published.Should().NotBe(
				default,
				$"{packageId}'s grace period is measured from its publication date, so a missing one grants an unbounded grace");
		}
	}

	[Fact]
	public void TheCommittedFloorCatalogueShouldParse()
	{
		var path = RepositoryRootFile.Resolve(NuGetFloorCatalog.FileName);
		path.Should().NotBeNull();
		File.Exists(path).Should().BeTrue("the seeded floor catalogue is committed");

		var catalog = new NuGetFloorCatalog(path);

		catalog.LoadFailed.Should().BeFalse(
			"a catalogue that will not parse stands the consistency half of the gate down: " + catalog.LoadFailure);
		catalog.PackageIds.Should().NotBeEmpty("no floors means every repository passes, however far behind it is");
	}

	[Fact]
	public void EveryCommittedFloorShouldBeAStableVersion()
	{
		var catalog = new NuGetFloorCatalog(RepositoryRootFile.Resolve(NuGetFloorCatalog.FileName));

		foreach (var packageId in catalog.PackageIds)
		{
			var floor = catalog.GetFloor(packageId);
			floor.Should().NotBeNullOrWhiteSpace();
			floor.Should().NotContain(
				"-",
				$"{packageId}'s floor must not be a prerelease: it would fail every repository on the stable version");
		}
	}
}
