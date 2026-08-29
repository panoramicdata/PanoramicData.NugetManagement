using System.Xml.Linq;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for reading a package's declared repository out of its nuspec. PanoramicData.EPPlus declares
/// panoramicdata/PanoramicData.EPPlus and links rimland/EPPlus — the upstream it was forked from — as
/// its project URL. Discovery followed the project URL, so seven governance runs were applied to a
/// clone of someone else's repository.
/// </summary>
public class RepositoryResolutionTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _epplusNuspec = """
		<?xml version="1.0" encoding="utf-8"?>
		<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
		  <metadata>
			<id>PanoramicData.EPPlus</id>
			<version>10.1.1</version>
			<projectUrl>https://github.com/rimland/EPPlus</projectUrl>
			<repository type="git" url="https://github.com/panoramicdata/PanoramicData.EPPlus" commit="5ad2c0de" />
		  </metadata>
		</package>
		""";

	[Fact]
	public void TheDeclaredRepositoryShouldWin_WhenTheProjectUrlPointsSomewhereElse()
	{
		RepositoryUrlFrom(_epplusNuspec)
			.Should().Be("https://github.com/panoramicdata/PanoramicData.EPPlus",
				"the repository element is the publisher saying where the source is; projectUrl is a documentation link");
	}

	[Fact]
	public void NothingShouldBeReturned_WhenNoRepositoryIsDeclared()
	{
		var nuspec = """
			<?xml version="1.0" encoding="utf-8"?>
			<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
			  <metadata>
				<id>Acme.Widget</id>
				<projectUrl>https://github.com/acme/Widget</projectUrl>
			  </metadata>
			</package>
			""";

		RepositoryUrlFrom(nuspec).Should().BeNull("the caller falls back to the project link");
	}

	[Fact]
	public void TheNamespaceShouldNotMatter()
	{
		// Nuspecs appear with and without the schema namespace, and across schema versions.
		var nuspec = """
			<package>
			  <metadata>
				<repository type="git" url="https://github.com/acme/Widget" />
			  </metadata>
			</package>
			""";

		RepositoryUrlFrom(nuspec).Should().Be("https://github.com/acme/Widget");
	}

	/// <summary>
	/// The extraction NuGetDiscoveryService performs on a fetched nuspec.
	/// </summary>
	private static string? RepositoryUrlFrom(string nuspec)
		=> XDocument.Parse(nuspec)
			.Descendants()
			.FirstOrDefault(element => string.Equals(element.Name.LocalName, "repository", StringComparison.OrdinalIgnoreCase))
			?.Attribute("url")?.Value;
}
