using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ToolbarScope"/>: which selections act on the whole estate, which steps are
/// willing to be run that way, and which repositories are left out when they are.
/// </summary>
public class ToolbarScopeTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryDashboardRow Row(
		string name,
		bool cloned = true,
		bool governed = true) => new()
		{
			RepositoryFullName = $"panoramicdata/{name}",
			Organization = "panoramicdata",
			IsClonedLocally = cloned,
			IsGoverned = governed
		};

	private static List<RepositoryDashboardRow> Targets(
		WorkflowStep step,
		IEnumerable<RepositoryDashboardRow> rows,
		params string[] excluded)
		=> ToolbarScope.Targets(
			rows,
			step,
			name => excluded.Contains(name, StringComparer.OrdinalIgnoreCase));

	[Fact]
	public void TheRepositoriesBranch_ActsOnTheWholeEstate()
		=> ToolbarScope.IsEstateWide(NavView.Repositories).Should().BeTrue();

	[Theory]
	[InlineData(NavView.RepositoryDetail)]
	[InlineData(NavView.PackageDetail)]
	[InlineData(NavView.Home)]
	[InlineData(NavView.Settings)]
	public void EveryOtherSelection_ActsOnWhateverIsSelected(NavView view)
		=> ToolbarScope.IsEstateWide(view).Should().BeFalse(
			"only the Repositories branch stands for the whole estate — the organisation node shares "
			+ "NavView.Home with the landing page, so it cannot be told apart by view");

	[Theory]
	[InlineData(WorkflowStep.GitSync)]
	[InlineData(WorkflowStep.Reassess)]
	[InlineData(WorkflowStep.Fix)]
	[InlineData(WorkflowStep.Build)]
	[InlineData(WorkflowStep.Test)]
	[InlineData(WorkflowStep.CommitAndPush)]
	public void EveryStepButPublish_MayBeRunAcrossTheEstate(WorkflowStep step)
		=> ToolbarScope.AllowsEstateWide(step).Should().BeTrue();

	[Fact]
	public void Publish_IsNeverRunAcrossTheEstate()
	{
		ToolbarScope.AllowsEstateWide(WorkflowStep.Publish).Should().BeFalse(
			"a package pushed to nuget.org cannot be taken back, and its version number can never be "
			+ "reused, so it is not a mistake one button press should be able to make forty times");

		Targets(WorkflowStep.Publish, [Row("A"), Row("B")]).Should().BeEmpty();
	}

	[Fact]
	public void AnExcludedRepository_TakesNoPart()
		=> Targets(WorkflowStep.Build, [Row("A"), Row("B")], "panoramicdata/B")
			.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/A");

	[Fact]
	public void ARepositoryWithNoClone_IsSkipped()
		=> Targets(WorkflowStep.Build, [Row("A"), Row("B", cloned: false)])
			.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/A",
				"there is nothing on disk to build");

	[Fact]
	public void AnUngovernedRepository_IsSkipped()
		=> Targets(WorkflowStep.Build, [Row("A"), Row("B", governed: false)])
			.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/A");

	[Fact]
	public void WhatItWillDo_SaysHowManyAndWhatItLeftOut()
		=> ToolbarScope.Describe("Build", targetCount: 12, candidateCount: 47)
			.Should().Contain("12 repositories")
			.And.Contain("35 repositories skipped",
				"acting on twelve of forty-seven without saying so is how a bulk action lies");

	[Fact]
	public void WhatItWillDo_StaysQuietWhenNothingWasLeftOut()
		=> ToolbarScope.Describe("Build", targetCount: 47, candidateCount: 47)
			.Should().Contain("47 repositories")
			.And.NotContain("skipped");

	[Fact]
	public void WhatItWillDo_SaysSoWhenThereIsNothingToDo()
		=> ToolbarScope.Describe("Build", targetCount: 0, candidateCount: 3)
			.Should().Contain("nothing to act on");
}
