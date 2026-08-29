using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for finding clones the app made of repositories that are not ours. Clones are filed by
/// owner, so a repository governed by mistake leaves an owner folder behind — rimland and
/// datahint-eu both sat in the clone root, holding real checkouts of other people's code, long
/// after the rows that produced them were gone.
/// </summary>
public class StrayCloneScannerTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void OwnersOutsideOurOrganisationsShouldBeFound()
	{
		GivenClone("panoramicdata", "Meraki.Api");
		GivenClone("datahint-eu", "vizor-echarts");
		GivenClone("rimland", "EPPlus");

		StrayCloneScanner.FindStrayClones(_root, ["panoramicdata"])
			.Select(clone => clone.Owner)
			.Should().BeEquivalentTo(["datahint-eu", "rimland"]);
	}

	[Fact]
	public void OurOwnOwnerFolderShouldNeverBeStray()
	{
		GivenClone("panoramicdata", "Meraki.Api");

		StrayCloneScanner.FindStrayClones(_root, ["panoramicdata"]).Should().BeEmpty();
	}

	[Fact]
	public void CapitalisationShouldNotMakeAnOwnerStray()
	{
		GivenClone("PanoramicData", "Meraki.Api");

		StrayCloneScanner.FindStrayClones(_root, ["panoramicdata"])
			.Should().BeEmpty("GitHub organisation names are case-insensitive");
	}

	[Fact]
	public void AStrayOwnerShouldCarryItsPathAndWhatIsInIt()
	{
		GivenClone("datahint-eu", "vizor-echarts");
		GivenClone("datahint-eu", "something-else");

		var stray = StrayCloneScanner.FindStrayClones(_root, ["panoramicdata"]).Should().ContainSingle().Subject;

		stray.Path.Should().Be(Path.Combine(_root, "datahint-eu"));
		stray.RepositoryNames.Should().BeEquivalentTo(["vizor-echarts", "something-else"]);
	}

	[Fact]
	public void ACloneRootThatIsNotThereShouldFindNothing()
		=> StrayCloneScanner.FindStrayClones(Path.Combine(_root, "absent"), ["panoramicdata"])
			.Should().BeEmpty("a missing clone root is nothing to report, not an error");

	private void GivenClone(string owner, string name)
		=> Directory.CreateDirectory(Path.Combine(_root, owner, name, ".git"));

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
			{
				Directory.Delete(_root, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
