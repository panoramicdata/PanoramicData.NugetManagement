using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that many packages from one repository make one row. PanoramicData.ECharts publishes four,
/// and until now each was cloned, assessed and remediated separately — the same repository, the same
/// findings, four times over.
/// </summary>
public class RepositoryGroupingTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly string[] _organizations = ["panoramicdata"];

	[Fact]
	public void FourPackagesFromOneRepositoryShouldMakeOneRow()
		=> Build(EChartsPackages()).Rows.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/PanoramicData.ECharts");

	[Fact]
	public void TheRowShouldListEveryPackageItPublishes()
		=> Build(EChartsPackages()).Rows.Single().Packages
			.Select(package => package.PackageId)
			.Should().BeEquivalentTo(
			[
				"PanoramicData.ECharts",
				"PanoramicData.ECharts.BindingGenerator",
				"PanoramicData.ECharts.Samples",
				"PanoramicData.ECharts.Sandbox"
			]);

	[Fact]
	public void EachPackageShouldKeepItsOwnVersion()
		=> Build(EChartsPackages()).Rows.Single().Packages
			.Single(package => package.PackageId == "PanoramicData.ECharts.Samples")
			.LatestVersion.Should().Be("1.4.0");

	[Fact]
	public void RepositoriesShouldBeGroupedRegardlessOfCase()
	{
		var packages = new List<NuGetPackageInfo>
		{
			Package("MagicSuite.Api", "https://github.com/panoramicdata/MagicSuite", "2.0.0"),
			Package("MagicSuite.Client", "https://github.com/PanoramicData/magicsuite", "2.0.1")
		};

		Build(packages).Rows.Should().ContainSingle("owner/name differing only in case is one repository");
	}

	[Fact]
	public void APackageDeclaringNothingShouldBeUngovernedRatherThanARow()
	{
		var result = Build([Package("JiraSetup", repositoryUrl: null, "1.0.0")]);

		result.Rows.Should().BeEmpty();
		result.Ungoverned.Should().ContainSingle()
			.Which.Reason.Should().Contain("declares no repository");
	}

	[Fact]
	public void APackageWhoseRepositoryIsSomebodyElsesShouldBeUngoverned()
	{
		var result = Build([Package("Vizor.ECharts.Net80", "https://github.com/datahint-eu/vizor-echarts", "1.0.0")]);

		result.Rows.Should().BeEmpty();
		result.Ungoverned.Should().ContainSingle()
			.Which.Reason.Should().Contain("datahint-eu/vizor-echarts");
	}

	private static (List<RepositoryDashboardRow> Rows, List<UngovernedPackage> Ungoverned) Build(
		List<NuGetPackageInfo> packages)
		=> DashboardService.BuildRows(packages, [], _organizations);

	private static List<NuGetPackageInfo> EChartsPackages() =>
	[
		Package("PanoramicData.ECharts", "https://github.com/panoramicdata/PanoramicData.ECharts", "1.4.2"),
		Package("PanoramicData.ECharts.BindingGenerator", "https://github.com/panoramicdata/PanoramicData.ECharts", "1.4.2"),
		Package("PanoramicData.ECharts.Samples", "https://github.com/panoramicdata/PanoramicData.ECharts", "1.4.0"),
		Package("PanoramicData.ECharts.Sandbox", "https://github.com/panoramicdata/PanoramicData.ECharts", "1.4.2")
	];

	private static NuGetPackageInfo Package(string packageId, string? repositoryUrl, string version)
		=> new()
		{
			PackageId = packageId,
			LatestVersion = version,
			Organization = "panoramicdata",
			RepositoryUrl = repositoryUrl,
			RepositoryOwner = GitHubRepositoryUrl.Owner(repositoryUrl),
			RepositoryName = GitHubRepositoryUrl.Name(repositoryUrl),
			ResolutionOutcome = repositoryUrl is null
				? RepositoryResolutionOutcome.NotDeclared
				: RepositoryResolutionOutcome.Resolved
		};
}
