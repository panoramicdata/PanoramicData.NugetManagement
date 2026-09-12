using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="NbgvVersionReader"/>, which reads the version nbgv reports for a working
/// tree out of its JSON output.
/// </summary>
/// <remarks>
/// The sample is real output from `nbgv get-version -f json` against panoramicdata/Uk.Parliament on
/// 2026-09-12, trimmed of the fields nothing reads. Parsing is kept apart from launching the process
/// so the part with a decision in it can be tested without one.
/// </remarks>
public class NbgvVersionReaderTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _realOutput = """
		{
		  "CloudBuildNumber": "10.1.14",
		  "CloudBuildNumberEnabled": false,
		  "VersionFileFound": true,
		  "AssemblyInformationalVersion": "10.1.14+bf9f276883",
		  "PublicRelease": true,
		  "SimpleVersion": "10.1.14",
		  "NuGetPackageVersion": "10.1.14",
		  "MajorMinorVersion": "10.1",
		  "VersionHeight": 14
		}
		""";

	[Fact]
	public void ReadsTheVersionTheNextReleaseWouldCarry()
	{
		NbgvVersionReader.ReadVersion(_realOutput).Should().Be("10.1.14");
	}

	[Fact]
	public void ReportsNothing_WhenTheRepositoryHasNoVersionFile()
	{
		// nbgv still answers for a repository with no version.json, with a version nothing asked for.
		// A rule comparing that against a tag would report an invention.
		var noVersionFile = """
			{
			  "VersionFileFound": false,
			  "NuGetPackageVersion": "0.0.1",
			  "SimpleVersion": "0.0.1"
			}
			""";

		NbgvVersionReader.ReadVersion(noVersionFile).Should().BeNull();
	}

	[Fact]
	public void ReportsNothing_WhenTheOutputIsNotJson()
	{
		// What an nbgv that failed actually leaves on stdout.
		NbgvVersionReader.ReadVersion("Unable to find a git repository.").Should().BeNull();
	}

	[Fact]
	public void ReportsNothing_WhenThereIsNoOutputAtAll()
	{
		NbgvVersionReader.ReadVersion(null).Should().BeNull();
		NbgvVersionReader.ReadVersion("").Should().BeNull();
	}

	[Fact]
	public void PrefersTheInstalledToolPath_BecauseNbgvIsNotReliablyOnPath()
	{
		// nbgv is a global dotnet tool. It resolved from one shell on this machine and not from
		// another, so the executable is named where it is actually installed rather than trusted to
		// the PATH of whatever process the app happens to be.
		var resolved = NbgvVersionReader.ResolveExecutable(
			@"C:\Users\someone",
			path => path == @"C:\Users\someone\.dotnet\tools\nbgv.exe");

		resolved.Should().Be(@"C:\Users\someone\.dotnet\tools\nbgv.exe");
	}

	[Fact]
	public void FallsBackToThePath_WhenTheToolIsNotWhereItIsUsuallyInstalled()
	{
		// A machine that installed it some other way still works; one without it at all fails to
		// launch, which the caller reads as "no version known".
		NbgvVersionReader.ResolveExecutable(@"C:\Users\someone", _ => false).Should().Be("nbgv");
	}

	[Fact]
	public void FallsBackToThePath_WhenThereIsNoHomeDirectory()
	{
		NbgvVersionReader.ResolveExecutable(null, _ => true).Should().Be("nbgv");
	}

	[Fact]
	public void ReportsNothing_WhenTheVersionFieldIsMissing()
	{
		NbgvVersionReader.ReadVersion("""{ "VersionFileFound": true }""").Should().BeNull();
	}

	[Fact]
	public void PrefersTheNuGetPackageVersion_WhichIsWhatAReleaseWouldPublish()
	{
		// The three version fields agree on a plain release build and diverge on a prerelease: the one
		// worth comparing with a tag is the one a release would actually publish.
		var prerelease = """
			{
			  "VersionFileFound": true,
			  "CloudBuildNumber": "2.1.9-beta",
			  "SimpleVersion": "2.1.9",
			  "NuGetPackageVersion": "2.1.9-beta"
			}
			""";

		NbgvVersionReader.ReadVersion(prerelease).Should().Be("2.1.9-beta");
	}
}
