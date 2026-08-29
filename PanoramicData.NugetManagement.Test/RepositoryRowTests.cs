using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the row that is a repository rather than a package. A repository publishing four
/// packages has four versions, and the row that flattened them to one could only ever be right
/// about the first.
/// </summary>
public class RepositoryRowTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void APackageMatchingTheTagShouldSaySo()
		=> new PublishedPackage { PackageId = "A", LatestVersion = "1.4.2" }
			.MatchesTag("1.4.2").Should().BeTrue();

	[Fact]
	public void APackageBehindTheTagShouldSaySo()
		=> new PublishedPackage { PackageId = "A", LatestVersion = "1.4.0" }
			.MatchesTag("1.4.2").Should().BeFalse();

	[Fact]
	public void APackageWithNoKnownTagShouldSayNothing()
		=> new PublishedPackage { PackageId = "A", LatestVersion = "1.4.0" }
			.MatchesTag(null).Should().BeNull();

	[Fact]
	public void ARepositoryShouldReportWhenAnyOfItsPackagesIsOutOfStep()
	{
		var row = new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/PanoramicData.ECharts",
			LatestTag = "1.4.2",
			Packages =
			[
				new() { PackageId = "PanoramicData.ECharts", LatestVersion = "1.4.2" },
				new() { PackageId = "PanoramicData.ECharts.Samples", LatestVersion = "1.4.0" }
			]
		};

		row.AnyPackageOutOfStepWithTag.Should().BeTrue();
	}

	[Fact]
	public void ARepositoryWhosePackagesAllMatchShouldBeInStep()
	{
		var row = new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/Meraki.Api",
			LatestTag = "1.4.2",
			Packages = [new() { PackageId = "Meraki.Api", LatestVersion = "1.4.2" }]
		};

		row.AnyPackageOutOfStepWithTag.Should().BeFalse();
	}

	[Fact]
	public void ARepositoryWithNoKnownTagShouldNotClaimItsPackagesAreOutOfStep()
	{
		var row = new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/Meraki.Api",
			Packages = [new() { PackageId = "Meraki.Api", LatestVersion = "1.4.2" }]
		};

		row.AnyPackageOutOfStepWithTag.Should().BeFalse("an unknown tag disagrees with nothing");
	}

	[Fact]
	public void AnUnassessedRepositoryShouldBeUnknown()
		=> new RepositoryDashboardRow { RepositoryFullName = "panoramicdata/Meraki.Api" }
			.HealthStatus.Should().Be(PackageHealthStatus.Unknown);
}
