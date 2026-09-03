using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the record of which packages the estate publishes itself. A package of ours needs no grace
/// period against nuget.org: we published it, so waiting to see whether it holds up is waiting on
/// ourselves.
/// </summary>
public class NuGetOwnedPackageCatalogTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void AnUnrecordedPackageShouldNotBeOurs()
		=> new NuGetOwnedPackageCatalog(null).Contains("Octokit").Should().BeFalse();

	[Fact]
	public void ARecordedPackageShouldBeOurs()
	{
		var catalog = new NuGetOwnedPackageCatalog(null);

		catalog.Record(["PanoramicData.SheetMagic"]);

		catalog.Contains("PanoramicData.SheetMagic").Should().BeTrue();
	}

	[Fact]
	public void PackageIdsShouldMatchRegardlessOfCase()
	{
		// Package ids arrive from NuGet search, from Directory.Packages.props and from Dependabot
		// titles, and nothing guarantees the three agree on casing.
		var catalog = new NuGetOwnedPackageCatalog(null);

		catalog.Record(["PanoramicData.SheetMagic"]);

		catalog.Contains("panoramicdata.sheetmagic").Should().BeTrue();
	}

	[Fact]
	public void RecordingShouldPersistForTheNextRun()
	{
		var path = Path.Combine(_directory, NuGetOwnedPackageCatalog.FileName);
		Directory.CreateDirectory(_directory);

		new NuGetOwnedPackageCatalog(path).Record(["AutoTask.Api", "Highlight.Api"]);

		new NuGetOwnedPackageCatalog(path).PackageIds
			.Should().BeEquivalentTo(["AutoTask.Api", "Highlight.Api"]);
	}

	[Fact]
	public void ALaterDiscoveryShouldNotForgetAPackageItDidNotReturn()
	{
		// Discovery pages through NuGet search. A truncated or throttled sweep returning fewer
		// packages must not quietly restore the grace period on the ones it missed.
		var path = Path.Combine(_directory, NuGetOwnedPackageCatalog.FileName);
		Directory.CreateDirectory(_directory);

		new NuGetOwnedPackageCatalog(path).Record(["AutoTask.Api", "Highlight.Api"]);
		new NuGetOwnedPackageCatalog(path).Record(["AutoTask.Api"]);

		new NuGetOwnedPackageCatalog(path).Contains("Highlight.Api")
			.Should().BeTrue("a package we have published stays ours");
	}

	[Fact]
	public void RecordingNothingNewShouldNotRewriteTheFile()
	{
		var path = Path.Combine(_directory, NuGetOwnedPackageCatalog.FileName);
		Directory.CreateDirectory(_directory);
		new NuGetOwnedPackageCatalog(path).Record(["AutoTask.Api"]);
		var written = File.GetLastWriteTimeUtc(path);

		new NuGetOwnedPackageCatalog(path).Record(["AutoTask.Api"]);

		File.GetLastWriteTimeUtc(path).Should().Be(written, "an unchanged committed file should not churn");
	}

	[Fact]
	public void ACorruptFileShouldTreatEveryPackageAsNotOurs()
	{
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, NuGetOwnedPackageCatalog.FileName);
		File.WriteAllText(path, "{ not json");

		new NuGetOwnedPackageCatalog(path).Contains("AutoTask.Api")
			.Should().BeFalse("failing to read it must restore the grace period, not remove it");
	}

	[Fact]
	public void ACorruptFileShouldSayThatItFailedToLoad()
	{
		Directory.CreateDirectory(_directory);
		var path = Path.Combine(_directory, NuGetOwnedPackageCatalog.FileName);
		File.WriteAllText(path, "{ not json");

		var catalog = new NuGetOwnedPackageCatalog(path);

		catalog.LoadFailed.Should().BeTrue();
		catalog.LoadFailure.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void AnAbsentFileShouldNotCountAsALoadFailure()
		=> new NuGetOwnedPackageCatalog(Path.Combine(_directory, NuGetOwnedPackageCatalog.FileName))
			.LoadFailed.Should().BeFalse("nothing has been discovered yet, which is normal");

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
