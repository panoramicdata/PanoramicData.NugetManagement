using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that a bad afternoon on nuget.org cannot shrink the estate. A repository governed yesterday
/// must not disappear because one small request went astray, and must never be blamed for an
/// omission its nuspec did not make.
/// </summary>
public class LookupFailureCarryForwardTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly string[] _organizations = ["panoramicdata"];

	[Fact]
	public void AFailedLookupShouldKeepTheRepositoryItHadYesterday()
	{
		var result = DashboardService.BuildRows([Unreadable("ConnectWise.Manage.Api")], Previously(), _organizations);

		result.Rows.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/ConnectWise.Manage.Api");
		result.Ungoverned.Should().BeEmpty();
	}

	[Fact]
	public void AFailedLookupWithNothingToFallBackOnShouldNotBlameTheNuspec()
	{
		var result = DashboardService.BuildRows([Unreadable("Brand.New.Api")], [], _organizations);

		result.Rows.Should().BeEmpty();
		var ungoverned = result.Ungoverned.Should().ContainSingle().Subject;
		ungoverned.Reason.Should().Contain("Could not read the nuspec");
		ungoverned.Reason.Should().NotContain(
			"declares no repository",
			"we did not read the nuspec, so we cannot say what it declares");
	}

	[Fact]
	public void ANuspecReadAndFoundSilentShouldNotCarryForward()
	{
		var silent = new NuGetPackageInfo
		{
			PackageId = "ConnectWise.Manage.Api",
			LatestVersion = "3.1.0",
			Organization = "panoramicdata",
			ResolutionOutcome = RepositoryResolutionOutcome.NotDeclared
		};

		var result = DashboardService.BuildRows([silent], Previously(), _organizations);

		result.Rows.Should().BeEmpty("the nuspec was read, and it no longer declares a repository");
		result.Ungoverned.Should().ContainSingle()
			.Which.Reason.Should().Contain("declares no repository");
	}

	private static List<RepositoryDashboardRow> Previously() =>
	[
		new()
		{
			RepositoryFullName = "panoramicdata/ConnectWise.Manage.Api",
			Organization = "panoramicdata",
			Packages = [new() { PackageId = "ConnectWise.Manage.Api", LatestVersion = "3.0.74" }]
		}
	];

	private static NuGetPackageInfo Unreadable(string packageId)
		=> new()
		{
			PackageId = packageId,
			LatestVersion = "3.1.0",
			Organization = "panoramicdata",
			ResolutionOutcome = RepositoryResolutionOutcome.LookupFailed,
			ResolutionError = "The connection was closed."
		};
}
