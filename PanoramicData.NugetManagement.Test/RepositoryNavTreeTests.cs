using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the layer between the estate and its packages. The rules assess a repository, so the
/// categories hang off the repository; the packages it publishes are a branch of their own, and no
/// finding is shown twice. PanoramicData.ECharts publishes four packages and used to appear four
/// times, each reporting the same findings against the same repository.
/// </summary>
public class RepositoryNavTreeTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string _eCharts = "panoramicdata/PanoramicData.ECharts";

	private readonly string _cacheDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void OneRepositoryShouldAppearOnceHoweverManyPackagesItPublishes()
		=> BuildTree()
			.Where(item => item.ParentKey == NavTreeDataProvider.ReposKey("panoramicdata"))
			.Should().ContainSingle()
			.Which.Text.Should().Be("PanoramicData.ECharts", "the owner is already the node above");

	[Fact]
	public void ThePackagesShouldHangOffTheirOwnBranch()
		=> BuildTree()
			.Where(item => item.ParentKey == NavTreeDataProvider.PackagesKey(_eCharts))
			.Select(item => item.PackageId)
			.Should().BeEquivalentTo(
			[
				"PanoramicData.ECharts",
				"PanoramicData.ECharts.BindingGenerator",
				"PanoramicData.ECharts.Samples",
				"PanoramicData.ECharts.Sandbox"
			]);

	[Fact]
	public void ThePackagesBranchShouldCountWhatItHolds()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.PackagesKey(_eCharts))
			.Text.Should().Be("Packages (4)");

	[Fact]
	public void ThePackagesBranchShouldHangOffTheRepository()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.PackagesKey(_eCharts))
			.ParentKey.Should().Be(NavTreeDataProvider.RepoKey(_eCharts));

	[Fact]
	public void ARepositoryPublishingOnePackageShouldStillHaveTheBranch()
		=> BuildTree(single: true)
			.Should().Contain(
				item => item.Key == NavTreeDataProvider.PackagesKey("panoramicdata/Meraki.Api"),
				"the shape of the tree must not change under the reader");

	[Fact]
	public void OnlyTheRepositoryNodeShouldOfferTheRepositoryActions()
	{
		var items = BuildTree();

		items.Where(NavTreeDataProvider.IsRepositoryNode)
			.Should().ContainSingle("excluding a repository is not something one of its packages can do")
			.Which.RepositoryFullName.Should().Be(_eCharts);
	}

	[Fact]
	public void EveryNodeBeneathARepositoryShouldKnowWhichRepositoryItIs()
		=> BuildTree()
			.Where(item => item.ParentKey == NavTreeDataProvider.PackagesKey(_eCharts))
			.Should().OnlyContain(item => item.RepositoryFullName == _eCharts);

	[Fact]
	public void APackageShouldShowItsOwnVersion()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.PackageKey(_eCharts, "PanoramicData.ECharts.Samples"))
			.Text.Should().Contain("1.4.0");

	[Fact]
	public void APackageOutOfStepWithTheTagShouldBeMarked()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.PackageKey(_eCharts, "PanoramicData.ECharts.Samples"))
			.IconCss.Should().Contain("text-warning", "1.4.0 is not the 1.4.2 the tag points at");

	[Fact]
	public void APackageAtTheTagShouldNotBeMarked()
		=> BuildTree()
			.Single(item => item.Key == NavTreeDataProvider.PackageKey(_eCharts, "PanoramicData.ECharts.Sandbox"))
			.IconCss.Should().NotContain("text-warning");

	[Fact]
	public void TheRepositoryShouldBeReachableFromAnyOfItsNodeKeys()
	{
		NavTreeDataProvider.RepositoryFromKey(NavTreeDataProvider.RepoKey(_eCharts)).Should().Be(_eCharts);
		NavTreeDataProvider.RepositoryFromKey(NavTreeDataProvider.PackagesKey(_eCharts)).Should().Be(_eCharts);
		NavTreeDataProvider.RepositoryFromKey(NavTreeDataProvider.PackageKey(_eCharts, "Any.Package")).Should().Be(_eCharts);
		NavTreeDataProvider.RepositoryFromKey(NavTreeDataProvider.RuleKey(_eCharts, "LIC-01")).Should().Be(_eCharts);
	}

	[Fact]
	public void AKeyAboveTheRepositoryLayerShouldNameNoRepository()
		=> NavTreeDataProvider.RepositoryFromKey(NavTreeDataProvider.OrgKey("panoramicdata"))
			.Should().BeNull();

	private List<NavItem> BuildTree(bool single = false)
	{
		var rows = single
			? new List<RepositoryDashboardRow>
			{
				new()
				{
					RepositoryFullName = "panoramicdata/Meraki.Api",
					Organization = "panoramicdata",
					Packages = [new() { PackageId = "Meraki.Api", LatestVersion = "1.0.0" }]
				}
			}
			:
			[
				new()
				{
					RepositoryFullName = _eCharts,
					Organization = "panoramicdata",
					LatestTag = "1.4.2",
					Packages =
					[
						new() { PackageId = "PanoramicData.ECharts", LatestVersion = "1.4.2" },
						new() { PackageId = "PanoramicData.ECharts.BindingGenerator", LatestVersion = "1.4.2" },
						new() { PackageId = "PanoramicData.ECharts.Samples", LatestVersion = "1.4.0" },
						new() { PackageId = "PanoramicData.ECharts.Sandbox", LatestVersion = "1.4.2" }
					]
				}
			];

		Directory.CreateDirectory(_cacheDirectory);
		var cache = new DashboardCacheService(
			NullLogger<DashboardCacheService>.Instance,
			Path.Combine(_cacheDirectory, "dashboard-cache.json"));
		cache.SetRows(rows);

		var settings = Options.Create(new AppSettings { NuGetOrganization = "panoramicdata" });

		return new NavTreeDataProvider(
			cache,
			new RuntimeSettingsService(settings, NullLogger<RuntimeSettingsService>.Instance),
			settings).BuildNavItems();
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_cacheDirectory))
			{
				Directory.Delete(_cacheDirectory, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
