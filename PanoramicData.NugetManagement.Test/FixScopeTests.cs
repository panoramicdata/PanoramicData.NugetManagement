using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="FixScope"/>: what the one Fix button does, for each thing that can be
/// selected. Fix fixes everything under the selected node, so the mapping is the whole feature.
/// </summary>
public class FixScopeTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData(NavView.RepositoryDetail)]
	[InlineData(NavView.PackageDetail)]
	public void OnARepository_FixDoesEverythingBeneathIt(NavView view)
	{
		var scope = FixScope.For(view);

		scope.ApplyRemediations.Should().BeTrue("the failing rules are beneath a repository");
		scope.TriageDependabot.Should().BeTrue("so is its Dependabot inbox");
	}

	[Theory]
	[InlineData(NavView.RepositoryIssuesDetail)]
	[InlineData(NavView.RepositoryIssueDetail)]
	public void OnTheInbox_FixTriagesAndNothingElse(NavView view)
	{
		var scope = FixScope.For(view);

		scope.TriageDependabot.Should().BeTrue();
		scope.ApplyRemediations.Should().BeFalse(
			"no failing rule sits under a pull request, so rewriting files is not 'everything beneath' it");
	}

	[Theory]
	[InlineData(NavView.CategoryDetail)]
	[InlineData(NavView.RuleDetail)]
	public void OnACategoryOrRule_FixAppliesRemediationsOnly(NavView view)
	{
		var scope = FixScope.For(view);

		scope.ApplyRemediations.Should().BeTrue();
		scope.TriageDependabot.Should().BeFalse(
			"a rule's scope is the rule, and closing pull requests is not part of it");
	}

	[Theory]
	[InlineData(NavView.None)]
	[InlineData(NavView.Settings)]
	[InlineData(NavView.Issues)]
	public void WhereThereIsNothingToFix_FixDoesNothing(NavView view)
		=> FixScope.For(view).HasAnything.Should().BeFalse();

	[Fact]
	public void HasAnything_IsTrueWhenEitherHalfApplies()
	{
		FixScope.For(NavView.RepositoryDetail).HasAnything.Should().BeTrue();
		FixScope.For(NavView.RepositoryIssuesDetail).HasAnything.Should().BeTrue();
	}

	[Fact]
	public void AdoptionArrivesUnderTheExistingTriageAction()
	{
		FixScope.For(NavView.RepositoryDetail).TriageDependabot.Should().BeTrue();

		typeof(FixActions).GetProperties()
			.Select(p => p.Name)
			.Should().BeEquivalentTo(
				["ApplyRemediations", "TriageDependabot", "HasAnything"],
				"adopting a Dependabot bump is part of what triaging one means, so it arrives under the "
					+ "existing action. Fix is the only control that fixes things, and a third action "
					+ "here would be a new button in all but name");
	}
}
