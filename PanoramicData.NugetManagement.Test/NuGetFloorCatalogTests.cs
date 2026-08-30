using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the floor learned from the estate's own repositories: the highest version of a package any
/// repository has been seen to declare. A repository below it is behind something we have already
/// proven works, which is a fact about us rather than about nuget.org.
/// </summary>
public class NuGetFloorCatalogTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void AnUnseenPackageShouldHaveNoFloor()
		=> new NuGetFloorCatalog(null).GetFloor("Codacy.Api").Should().BeNull();

	[Fact]
	public void TheFirstObservationShouldNotRaiseTheFloorWithinTheSameRun()
	{
		// The floor used for pass/fail is frozen at load, so nothing observed during a run can change
		// that run's verdicts. Learning applies to the next run.
		var catalog = new NuGetFloorCatalog(null);

		catalog.Observe("Codacy.Api", "3.0.43");

		catalog.GetFloor("Codacy.Api").Should().BeNull("this run's floor was fixed when the file loaded");
	}

	[Fact]
	public void AHigherVersionShouldRaiseThePersistedFloor()
	{
		var path = Path.Combine(_directory, NuGetFloorCatalog.FileName);
		Directory.CreateDirectory(_directory);

		var catalog = new NuGetFloorCatalog(path);
		catalog.Observe("Codacy.Api", "3.0.43", "panoramicdata/Meraki.Api");

		new NuGetFloorCatalog(path).GetFloor("Codacy.Api").Should().Be("3.0.43");
	}

	[Fact]
	public void ALowerVersionShouldNeverLowerTheFloor()
	{
		var path = Path.Combine(_directory, NuGetFloorCatalog.FileName);
		Directory.CreateDirectory(_directory);

		new NuGetFloorCatalog(path).Observe("Codacy.Api", "3.0.43");

		var second = new NuGetFloorCatalog(path);
		second.Observe("Codacy.Api", "3.0.11");

		new NuGetFloorCatalog(path).GetFloor("Codacy.Api").Should().Be("3.0.43", "the floor is a ratchet");
	}

	[Fact]
	public void RaisingTheFloorShouldRecordWhichRepositoryDidIt()
	{
		var catalog = new NuGetFloorCatalog(null);
		catalog.Observe("Codacy.Api", "3.0.43", "panoramicdata/Meraki.Api");

		var bump = catalog.RecentBumps.Should().ContainSingle().Subject;
		bump.PackageId.Should().Be("Codacy.Api");
		bump.To.Should().Be("3.0.43");
		bump.Repository.Should().Be("panoramicdata/Meraki.Api");
	}

	[Fact]
	public void AnUnparseableVersionShouldBeIgnored()
	{
		var catalog = new NuGetFloorCatalog(null);

		catalog.Observe("Codacy.Api", "$(SomeMsBuildProperty)");

		catalog.RecentBumps.Should().BeEmpty();
	}

	[Fact]
	public void ACorruptFileShouldLeaveEveryFloorUnsetRatherThanThrow()
	{
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, NuGetFloorCatalog.FileName);
		File.WriteAllText(path, "{ not json");

		new NuGetFloorCatalog(path).GetFloor("Codacy.Api").Should().BeNull();
	}

	[Fact]
	public void APrereleasePinShouldNotRaiseTheFloor()
	{
		// One repository pinning PanoramicData.Blazor 11.0.0-beta.1 to try it out is greater than
		// 10.0.205, so an unguarded ratchet would raise the committed floor above every stable pin in
		// the estate and fail all of them at PKG-07 (Critical) with remediation text telling them to
		// adopt a beta. There is no un-lower path, so the only remedy would be editing the file by
		// hand. A beta has proven nothing, which is the whole justification for the ratchet.
		var path = Path.Combine(_directory, NuGetFloorCatalog.FileName);
		Directory.CreateDirectory(_directory);

		var catalog = new NuGetFloorCatalog(path);
		catalog.Observe("PanoramicData.Blazor", "10.0.205", "panoramicdata/Stable");
		catalog.Observe("PanoramicData.Blazor", "11.0.0-beta.1", "panoramicdata/Adventurous");

		new NuGetFloorCatalog(path).GetFloor("PanoramicData.Blazor")
			.Should().Be("10.0.205", "the floor must mean the same kind of version the cache means");
	}

	[Fact]
	public void APrereleaseShouldNotEvenBeRecordedAsABump()
	{
		var catalog = new NuGetFloorCatalog(null);

		catalog.Observe("PanoramicData.Blazor", "11.0.0-beta.1", "panoramicdata/Adventurous");

		catalog.RecentBumps.Should().BeEmpty("a prerelease is not a floor movement");
	}

	[Fact]
	public void AFirstPrereleaseObservationShouldNotCreateAFloor()
	{
		var path = Path.Combine(_directory, NuGetFloorCatalog.FileName);
		Directory.CreateDirectory(_directory);

		new NuGetFloorCatalog(path).Observe("PanoramicData.Blazor", "11.0.0-beta.1");

		new NuGetFloorCatalog(path).GetFloor("PanoramicData.Blazor").Should().BeNull();
	}

	[Fact]
	public void ACorruptFileShouldSayThatItFailedToLoad()
	{
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, NuGetFloorCatalog.FileName);
		File.WriteAllText(path, "{ not json");

		var catalog = new NuGetFloorCatalog(path);

		catalog.LoadFailed.Should().BeTrue("a silently empty catalogue looks exactly like a compliant estate");
		catalog.LoadFailure.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void AnAbsentFileShouldNotCountAsALoadFailure()
		=> new NuGetFloorCatalog(Path.Combine(_directory, NuGetFloorCatalog.FileName))
			.LoadFailed.Should().BeFalse("nothing has been learned yet, which is normal");

	[Fact]
	public void ObservedPackagesShouldBeVisibleToTheRefreshSweep()
	{
		var catalog = new NuGetFloorCatalog(null);

		catalog.Observe("Codacy.Api", "3.0.43");
		catalog.Observe("Octokit", "14.0.0");

		catalog.PackageIds.Should().BeEquivalentTo(["Codacy.Api", "Octokit"]);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_directory))
			{
				Directory.Delete(_directory, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
