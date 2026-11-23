using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// An archived repository is outside governance whichever way it says so: GitHub's flag, or its name.
/// </summary>
public class ArchivedRepositoryExclusionTests
{
	[Theory]
	[InlineData("panoramicdata/PanoramicData.DocMagic.Archived", false, true)]
	[InlineData("panoramicdata/old-thing-ARCHIVED", false, true)]
	[InlineData("panoramicdata/Meraki.Api", true, true)]
	[InlineData("panoramicdata/Meraki.Api", false, false)]
	public void ARepositoryIsArchivedWhenGitHubOrItsNameSaysSo(string repository, bool githubArchived, bool expected)
	{
		var row = new RepositoryDashboardRow { RepositoryFullName = repository, IsArchived = githubArchived };

		(row.AutoExclusionReason == "archived").Should().Be(expected);
	}

	[Fact]
	public void TheOrganisationNameAloneDoesNotMarkARepositoryArchived()
		=> RepositoryDashboardRow.NameMarksArchived("archived-org/Meraki.Api").Should().BeFalse("only the repository's own name counts");

	[Fact]
	public void AForkThatPublishesNothingIsStillExcludedAsAFork()
		=> new RepositoryDashboardRow { RepositoryFullName = "panoramicdata/Thing", IsFork = true, DeclaredPackageIds = ["Thing"] }
			.AutoExclusionReason.Should().Be("fork");

	[Fact]
	public void AForkThatPublishesOurPackagesIsGoverned()
		=> new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/PanoramicData.EPPlus",
			IsFork = true,
			Packages = [new PublishedPackage { PackageId = "PanoramicData.EPPlus", LatestVersion = "11.0.59" }]
		}
			.AutoExclusionReason.Should().BeNull("a fork we publish from has become our own code");

	[Fact]
	public void AnArchivedForkThatPublishesOurPackagesIsStillExcludedAsArchived()
		=> new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/PanoramicData.EPPlus",
			IsFork = true,
			IsArchived = true,
			Packages = [new PublishedPackage { PackageId = "PanoramicData.EPPlus" }]
		}
			.AutoExclusionReason.Should().Be("archived");

	[Theory]
	[InlineData(true, "excluded by a person")]
	[InlineData(false, null)]
	public void APersonCanExcludeAnyRepositoryToo(bool userExcluded, string? expected)
		=> AgentApi.ExclusionReason(new RepositoryDashboardRow { RepositoryFullName = "panoramicdata/Meraki.Api" }, userExcluded)
			.Should().Be(expected);

	[Fact]
	public void ATestQueuedByAnAgentIsAlwaysSandboxed()
		=> AgentApi.QueueParameters(WorkKind.Test, null).Should().ContainSingle().Which.Should().Be(("sandbox", "true"));

	[Fact]
	public void ARuleFixCarriesItsRuleAndNothingElse()
		=> AgentApi.QueueParameters(WorkKind.FixRule, "ci-17").Should().ContainSingle().Which.Should().Be(("ruleId", "CI-17"));

	[Fact]
	public void OtherWorkCarriesNoParameters()
		=> AgentApi.QueueParameters(WorkKind.Build, null).Should().BeEmpty();

	[Fact]
	public void AnArchivedRepositoryReportsArchivedWhateverElseIsTrue()
		=> AgentApi.ExclusionReason(
				new RepositoryDashboardRow { RepositoryFullName = "panoramicdata/PanoramicData.DocMagic.Archived", IsFork = true },
				userExcluded: true)
			.Should().Be("archived");
}
