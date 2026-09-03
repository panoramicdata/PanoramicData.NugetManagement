using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that package discovery is what teaches the rules which packages the estate publishes.
/// </summary>
/// <remarks>
/// Discovery already searches NuGet for <c>owner:</c> each organisation under management, so it holds
/// the answer the freshness rules need and nothing else has to go and ask.
/// </remarks>
public class OwnedPackageDiscoveryTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void DiscoveryShouldRecordEveryPackageItFoundIncludingOnesNoRepositoryClaims()
	{
		// A package of ours that maps to no repository we govern is still a package of ours: whether we
		// can find its repository is a separate question from whether we published it, and only the
		// second one decides the grace period.
		var owned = new NuGetOwnedPackageCatalog(null);

		DashboardService.RecordOwnedPackages(
			owned,
			[
				Package("PanoramicData.SheetMagic", "https://github.com/panoramicdata/PanoramicData.SheetMagic"),
				Package("Orphaned.Api", repositoryUrl: null)
			]);

		owned.PackageIds.Should().BeEquivalentTo(["PanoramicData.SheetMagic", "Orphaned.Api"]);
	}

	private static NuGetPackageInfo Package(string packageId, string? repositoryUrl)
		=> new()
		{
			PackageId = packageId,
			LatestVersion = "1.0.0",
			Organization = "panoramicdata",
			RepositoryUrl = repositoryUrl
		};
}
