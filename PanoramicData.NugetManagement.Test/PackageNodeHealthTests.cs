using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the colour of the Packages branch and the package nodes beneath it.
/// </summary>
/// <remarks>
/// MatchesTag is deliberately three-state — matching, disagreeing, or unknown because a version or a
/// tag is missing — but the tree used to colour the first and the last identically, so a package at
/// exactly the tag looked the same as one nothing was known about. Grey has one meaning in this tree
/// and it is "unknown"; a package known to be in step earns green.
/// </remarks>
public class PackageNodeHealthTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _temporaryDirectory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		Guid.NewGuid().ToString("n"));

	private const string _repository = "panoramicdata/Athonet.Api";

	[Fact]
	public void APackageAtTheLatestTagShouldBeGreen()
		=> PackageIcon(BuildTree(latestTag: "1.0.47", versions: ["1.0.47"]), "Athonet.Api")
			.Should().Contain("text-success");

	[Fact]
	public void APackageBehindTheLatestTagShouldBeAmber()
		=> PackageIcon(BuildTree(latestTag: "1.0.48", versions: ["1.0.47"]), "Athonet.Api")
			.Should().Contain("text-warning");

	[Fact]
	public void APackageWithNoTagToCompareAgainstShouldStayGrey()
		=> PackageIcon(BuildTree(latestTag: null, versions: ["1.0.47"]), "Athonet.Api")
			.Should().Contain("text-muted", "nothing is known, which is what grey means here");

	[Fact]
	public void ThePackagesBranchShouldBeGreenWhenEveryPackageIsInStep()
		=> PackagesBranchIcon(BuildTree(latestTag: "1.0.47", versions: ["1.0.47"]))
			.Should().Contain("text-success");

	[Fact]
	public void ThePackagesBranchShouldBeAmberWhenAPackageIsOutOfStep()
		=> PackagesBranchIcon(BuildTree(latestTag: "1.0.48", versions: ["1.0.47"]))
			.Should().Contain("text-warning");

	[Fact]
	public void ThePackagesBranchShouldBeGreyWhenNothingCanBeCompared()
		=> PackagesBranchIcon(BuildTree(latestTag: null, versions: ["1.0.47"]))
			.Should().Contain("text-muted");

	[Fact]
	public void TheWorstPackageShouldDecideTheBranch()
		=> PackagesBranchIcon(BuildTree(latestTag: "1.0.47", versions: ["1.0.47", "0.9.0"]))
			.Should().Contain("text-warning", "one package out of step is enough to colour the branch");

	private static string PackageIcon(List<NavItem> items, string packageId)
		=> items.Single(item => item.Key == NavTreeDataProvider.PackageKey(_repository, packageId)).IconCss ?? "";

	private static string PackagesBranchIcon(List<NavItem> items)
		=> items.Single(item => item.Key == NavTreeDataProvider.PackagesKey(_repository)).IconCss ?? "";

	private List<NavItem> BuildTree(string? latestTag, string[] versions)
	{
		var rows = new List<RepositoryDashboardRow>
		{
			new()
			{
				Organization = "panoramicdata",
				RepositoryFullName = _repository,
				LatestTag = latestTag,
				Packages = [.. versions.Select((version, index) => new PublishedPackage
				{
					PackageId = index == 0 ? "Athonet.Api" : $"Athonet.Api.Extra{index}",
					LatestVersion = version
				})]
			}
		};

		Directory.CreateDirectory(_temporaryDirectory);
		var cache = new DashboardCacheService(
			NullLogger<DashboardCacheService>.Instance,
			Path.Combine(_temporaryDirectory, "dashboard-cache.json"));
		cache.SetRows(rows);

		var settings = Options.Create(new AppSettings { NuGetOrganization = "panoramicdata" });

		return new NavTreeDataProvider(
			cache,
			new RuntimeSettingsService(
				settings,
				NullLogger<RuntimeSettingsService>.Instance,
				Path.Combine(_temporaryDirectory, "runtime-settings.json")),
			settings).BuildNavItems();
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_temporaryDirectory))
			{
				Directory.Delete(_temporaryDirectory, recursive: true);
			}
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}
}
