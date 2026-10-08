using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ToolbarScope"/>: which selections act on many repositories, which steps are
/// willing to be run that way, and which of the ticked repositories are left out when they are.
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
			// An ordinary repository publishes a package; one that publishes nothing is assess-only.
			Packages = [new() { PackageId = name }],
			IsClonedLocally = cloned,
			IsGoverned = governed
		};

	private static RepositorySelection Ticked(params RepositoryDashboardRow[] rows)
	{
		var selection = new RepositorySelection();
		selection.SelectAll(rows.Select(row => row.RepositoryFullName));
		return selection;
	}

	private static List<RepositoryDashboardRow> Targets(
		WorkflowStep step,
		IEnumerable<RepositoryDashboardRow> rows,
		RepositorySelection selection,
		params string[] excluded)
		=> ToolbarScope.Targets(
			rows,
			step,
			name => excluded.Contains(name, StringComparer.OrdinalIgnoreCase),
			selection);

	[Theory]
	[InlineData(NavView.Repositories)]
	[InlineData(NavView.Issues)]
	[InlineData(NavView.Organisation)]
	public void TheEstateNodes_ActOnTheTickedRepositories(NavView view)
		=> ToolbarScope.IsEstateWide(view).Should().BeTrue();

	[Theory]
	[InlineData(NavView.RepositoryDetail)]
	[InlineData(NavView.PackageDetail)]
	[InlineData(NavView.Home)]
	[InlineData(NavView.Settings)]
	[InlineData(NavView.IssueCategoryDetail)]
	[InlineData(NavView.IssueRuleDetail)]
	public void EveryOtherSelection_ActsOnWhateverIsSelected(NavView view)
		=> ToolbarScope.IsEstateWide(view).Should().BeFalse(
			"the landing page selects nothing, a repository is one repository, and the per-category and "
			+ "per-rule issue views keep the bulk actions they already have");

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
	public void Publish_IsNeverRunAcrossTheEstate_EvenWhenTicked()
	{
		ToolbarScope.AllowsEstateWide(WorkflowStep.Publish).Should().BeFalse(
			"a package pushed to nuget.org cannot be taken back, and its version number can never be "
			+ "reused, so it is not a mistake one button press should be able to make forty times");

		var rows = new[] { Row("A"), Row("B") };

		Targets(WorkflowStep.Publish, rows, Ticked(rows)).Should().BeEmpty();
	}

	[Fact]
	public void WithNothingTicked_ABulkStepActsOnNothing()
	{
		var rows = new[] { Row("A"), Row("B") };

		Targets(WorkflowStep.Build, rows, new RepositorySelection()).Should().BeEmpty(
			"no ticks means nothing: one stray click on a forty-repository estate must not touch it");
	}

	[Fact]
	public void OnlyTheTickedRepositories_AreTargeted()
	{
		var rows = new[] { Row("A"), Row("B"), Row("C") };

		Targets(WorkflowStep.Build, rows, Ticked(rows[0], rows[2]))
			.Select(row => row.RepositoryName).Should().BeEquivalentTo(["A", "C"]);
	}

	[Fact]
	public void Reassess_WithNothingTicked_FallsBackToTheWholeOrganisation()
	{
		var rows = new[] { Row("A"), Row("B", cloned: false) };

		ToolbarScope.FallsBackToWholeOrganisation(WorkflowStep.Reassess).Should().BeTrue();

		Targets(WorkflowStep.Reassess, rows, new RepositorySelection())
			.Select(row => row.RepositoryName).Should().BeEquivalentTo(["A", "B"],
				"re-assessing is read-only and works remotely, so it needs no clone and no ticks");
	}

	[Theory]
	[InlineData(WorkflowStep.GitSync)]
	[InlineData(WorkflowStep.Fix)]
	[InlineData(WorkflowStep.Build)]
	[InlineData(WorkflowStep.Test)]
	[InlineData(WorkflowStep.CommitAndPush)]
	public void NoOtherStepFallsBackToTheWholeOrganisation(WorkflowStep step)
		=> ToolbarScope.FallsBackToWholeOrganisation(step).Should().BeFalse();

	[Fact]
	public void Reassess_NeedsNoClone_ButEveryOtherStepDoes()
	{
		ToolbarScope.RequiresClone(WorkflowStep.Reassess).Should().BeFalse();
		ToolbarScope.RequiresClone(WorkflowStep.Build).Should().BeTrue();
		ToolbarScope.RequiresClone(WorkflowStep.CommitAndPush).Should().BeTrue();
	}

	[Fact]
	public void ATickedRepositoryThatIsNowExcluded_TakesNoPart()
	{
		var rows = new[] { Row("A"), Row("B") };

		Targets(WorkflowStep.Build, rows, Ticked(rows), "panoramicdata/B")
			.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/A");
	}

	[Fact]
	public void ATickedRepositoryWithNoClone_IsSkipped()
	{
		var rows = new[] { Row("A"), Row("B", cloned: false) };

		Targets(WorkflowStep.Build, rows, Ticked(rows))
			.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/A", "there is nothing on disk to build");
	}

	[Fact]
	public void ATickedUngovernedRepository_IsSkipped()
	{
		var rows = new[] { Row("A"), Row("B", governed: false) };

		Targets(WorkflowStep.Build, rows, Ticked(rows))
			.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("panoramicdata/A");
	}

	[Fact]
	public void ASelectionNamedInAnotherCase_StillMatches()
	{
		var rows = new[] { Row("Cisco.Iq.Api") };
		var selection = new RepositorySelection();
		selection.Select("PANORAMICDATA/CISCO.IQ.API");

		Targets(WorkflowStep.Build, rows, selection).Should().ContainSingle();
	}

	[Fact]
	public void WhatItWillDo_SaysHowManyAndWhatItLeftOut()
		=> ToolbarScope.DescribeSelection("Build", targetCount: 12, selectedCount: 47)
			.Should().Contain("12 repositories")
			.And.Contain("35 ticked repositories skipped",
				"acting on twelve of forty-seven ticked without saying so is how a bulk action lies");

	[Fact]
	public void WhatItWillDo_StaysQuietWhenNothingWasLeftOut()
		=> ToolbarScope.DescribeSelection("Build", targetCount: 47, selectedCount: 47)
			.Should().Contain("47 repositories")
			.And.NotContain("skipped");

	[Fact]
	public void WhatItWillDo_WithNothingTicked_SaysToTickSome()
		=> ToolbarScope.DescribeSelection("Build", targetCount: 0, selectedCount: 0)
			.Should().Contain("tick repositories");

	[Fact]
	public void WhatItWillDo_WhenNoneOfTheTickedCanBeActedOn_SaysSo()
		=> ToolbarScope.DescribeSelection("Build", targetCount: 0, selectedCount: 3)
			.Should().Contain("nothing to act on").And.Contain("3 ticked repositories");

	[Fact]
	public void WhatItWillDo_UsesTheSingularForOne()
		=> ToolbarScope.DescribeSelection("Build", targetCount: 1, selectedCount: 1)
			.Should().Contain("1 repository.");

	[Theory]
	[InlineData(WorkflowStep.CommitAndPush, 2, true)]
	[InlineData(WorkflowStep.CommitAndPush, 1, false)]
	[InlineData(WorkflowStep.CommitAndPush, 0, false)]
	[InlineData(WorkflowStep.Build, 40, false)]
	[InlineData(WorkflowStep.GitSync, 40, false)]
	[InlineData(WorkflowStep.Test, 40, false)]
	[InlineData(WorkflowStep.Fix, 40, false)]
	public void OnlyCommitAndPush_AsksFirst_AndOnlyForMoreThanOneRepository(
		WorkflowStep step, int targets, bool expected)
		=> ToolbarScope.RequiresConfirmation(step, targets).Should().Be(expected);

	[Theory]
	[InlineData(2, true)]
	[InlineData(1, false)]
	[InlineData(0, false)]
	public void FixWithAi_AsksFirst_ForMoreThanOneRepository(int targets, bool expected)
		=> ToolbarScope.RequiresAiFixConfirmation(targets).Should().Be(expected,
			"one threshold for both, so the two guards cannot disagree");

	[Fact]
	public void TheConfirmationNamesTheCountAndTheRepositories()
		=> ToolbarScope.ConfirmationMessage("Commit & push", ["A.Api", "B.Api", "C.Api"])
			.Should().Be("Commit & push 3 repositories? A.Api, B.Api, C.Api.");

	[Fact]
	public void TheConfirmationTruncatesAVeryLongList()
	{
		var names = Enumerable.Range(1, 12).Select(i => $"Repo{i}").ToList();

		var message = ToolbarScope.ConfirmationMessage("Commit & push", names);

		message.Should().Contain("12 repositories").And.Contain("Repo8").And.NotContain("Repo9")
			.And.Contain("and 4 more");
	}
}
