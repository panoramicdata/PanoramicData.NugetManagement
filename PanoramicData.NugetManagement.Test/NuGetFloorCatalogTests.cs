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
