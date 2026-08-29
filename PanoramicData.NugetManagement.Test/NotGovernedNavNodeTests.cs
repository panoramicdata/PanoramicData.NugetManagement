using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the branch that accounts for packages we own whose repository we do not. Hiding them
/// silently would replace one confusion with another: a package would vanish from the estate with
/// nothing anywhere saying why, or which nuspec to go and fix.
/// </summary>
public class NotGovernedNavNodeTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _cacheDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	[Fact]
	public void AnUngovernedPackageShouldNotAppearAmongTheRepositories()
		=> BuildTree()
			.Where(item => item.ParentKey == NavTreeDataProvider.ReposKey("panoramicdata"))
			.Select(item => item.Text)
			.Should().BeEquivalentTo(["Meraki.Api"]);

	[Fact]
	public void TheBranchShouldCountWhatItHides()
		=> NotGovernedNode(BuildTree()).Text.Should().Be("Not governed (1)");

	[Fact]
	public void TheBranchShouldHangOffTheOrganisation()
		=> NotGovernedNode(BuildTree()).ParentKey.Should().Be(NavTreeDataProvider.OrgKey("panoramicdata"));

	[Fact]
	public void EachHiddenPackageShouldSayWhichRepositoryItDeclared()
	{
		var items = BuildTree();
		var child = items.Should().ContainSingle(
			item => item.ParentKey == NavTreeDataProvider.NotGovernedKey("panoramicdata")).Subject;

		child.Text.Should().Contain("Vizor.ECharts.Net80").And.Contain("datahint-eu/vizor-echarts");
		child.IsLeaf.Should().BeTrue();
	}

	[Fact]
	public void TheBranchShouldBeAbsentWhenEverythingIsGoverned()
	{
		var items = BuildTree(includeUngoverned: false);

		items.Should().NotContain(item => item.Key == NavTreeDataProvider.NotGovernedKey("panoramicdata"));
	}

	[Fact]
	public void TheBranchShouldNotColourTheOrganisation()
		=> BuildTree().Single(item => item.Key == NavTreeDataProvider.OrgKey("panoramicdata"))
			.IssueCount.Should().Be(0, "a nuspec to fix is not an issue against a repository we govern");

	private static NavItem NotGovernedNode(List<NavItem> items)
		=> items.Single(item => item.Key == NavTreeDataProvider.NotGovernedKey("panoramicdata"));

	private List<NavItem> BuildTree(bool includeUngoverned = true)
	{
		var rows = new List<PackageDashboardRow>
		{
			new()
			{
				PackageId = "Meraki.Api",
				Organization = "panoramicdata",
				RepositoryFullName = "panoramicdata/Meraki.Api"
			}
		};

		if (includeUngoverned)
		{
			rows.Add(new PackageDashboardRow
			{
				PackageId = "Vizor.ECharts.Net80",
				Organization = "panoramicdata",
				RepositoryFullName = "datahint-eu/vizor-echarts",
				IsGoverned = false,
				NotGovernedReason = "The nuspec declares datahint-eu/vizor-echarts, which is not one of our organisations.",
				Status = PackageStatus.NotGoverned
			});
		}

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
