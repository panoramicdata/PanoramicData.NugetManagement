using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for deciding whether a discovered package's repository is ours to govern. NuGet's
/// <c>owner:</c> search says who owns the package, not who owns the repository behind it: we own
/// Vizor.ECharts.Net80, whose nuspec correctly declares datahint-eu/vizor-echarts. Taking that
/// derived location on trust is how somebody else's repository came to be cloned and assessed.
/// </summary>
public class GovernanceScopeTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly string[] _organizations = ["panoramicdata"];

	[Fact]
	public void ARepositoryOwnedBySomebodyElseShouldNotBeGoverned()
		=> GovernanceScope.ReasonNotGoverned("datahint-eu/vizor-echarts", _organizations)
			.Should().NotBeNull("owning the package does not make the repository ours")
			.And.Contain("datahint-eu/vizor-echarts", "the reason names the nuspec to go and fix");

	[Fact]
	public void APackageDeclaringNoRepositoryShouldNotBeGoverned()
		=> GovernanceScope.ReasonNotGoverned(null, _organizations)
			.Should().NotBeNull()
			.And.Contain("no repository", "this is a distinct reason from a foreign owner")
			.And.NotContain("/");

	[Fact]
	public void OurOwnRepositoryShouldBeGoverned()
		=> GovernanceScope.ReasonNotGoverned("panoramicdata/Meraki.Api", _organizations)
			.Should().BeNull();

	[Fact]
	public void CapitalisationShouldNotDecideOwnership()
		=> GovernanceScope.ReasonNotGoverned("PanoramicData/Meraki.Api", _organizations)
			.Should().BeNull("GitHub organisation names are case-insensitive");

	[Fact]
	public void EveryConfiguredOrganisationShouldBeOurs()
		=> GovernanceScope.ReasonNotGoverned("acme/Widget", ["panoramicdata", "acme"])
			.Should().BeNull();

	[Fact]
	public void ABareRepositoryNameShouldNotBeGoverned()
		=> GovernanceScope.ReasonNotGoverned("Widget", _organizations)
			.Should().NotBeNull("a name with no owner names no repository we can prove is ours");

	[Fact]
	public void AForeignRepositoryShouldLeaveARowUngoverned()
	{
		var row = RowFor("datahint-eu/vizor-echarts");

		GovernanceScope.Apply(row, _organizations);

		row.IsGoverned.Should().BeFalse();
		row.NotGovernedReason.Should().Contain("datahint-eu/vizor-echarts");
		row.Status.Should().Be(PackageStatus.NotGoverned);
	}

	[Fact]
	public void AnUngovernedRowShouldNotBeTreatedAsCloned()
	{
		var row = RowFor("datahint-eu/vizor-echarts");
		row.IsClonedLocally = true;
		row.LocalPath = Path.Combine("clones", "datahint-eu", "vizor-echarts");

		GovernanceScope.Apply(row, _organizations);

		row.IsClonedLocally.Should().BeFalse("nothing may act on a checkout of a repository that is not ours");
		row.LocalPath.Should().BeNull();
	}

	[Fact]
	public void OurOwnRowShouldBeGovernedAndUntouched()
	{
		var row = RowFor("panoramicdata/Meraki.Api");
		row.IsClonedLocally = true;

		GovernanceScope.Apply(row, _organizations);

		row.IsGoverned.Should().BeTrue();
		row.NotGovernedReason.Should().BeNull();
		row.IsClonedLocally.Should().BeTrue();
	}

	[Fact]
	public void AnUngovernedRowShouldCarryNoAssessment()
	{
		var row = RowFor("datahint-eu/vizor-echarts");
		row.Assessment = new RepoAssessment
		{
			RepositoryFullName = "datahint-eu/vizor-echarts",
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = []
		};

		GovernanceScope.Apply(row, _organizations);

		row.Assessment.Should().BeNull(
			"findings against a repository we do not govern must not reach the counts, and a cached row can carry findings from when it was governed");
		row.TotalFailures.Should().Be(0);
	}

	private static PackageDashboardRow RowFor(string? repositoryFullName)
		=> new()
		{
			PackageId = "Some.Package",
			Organization = "panoramicdata",
			RepositoryFullName = repositoryFullName
		};
}
