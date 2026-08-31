using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="FixScope.Describe"/>: the sentence that tells the user what pressing Fix will
/// actually do, before they press it.
/// </summary>
public class FixPlanTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static string Describe(NavView view, int remediableRules, int dependabotPullRequests)
		=> FixScope.Describe(FixScope.For(view), remediableRules, dependabotPullRequests);

	[Fact]
	public void OnARepositoryWithBoth_ItNamesBothHalvesAndTheirCounts()
		=> Describe(NavView.RepositoryDetail, 7, 6)
			.Should().Be("Fix will apply 7 auto-fixes and triage 6 Dependabot pull requests.");

	[Fact]
	public void Singulars_ReadProperly()
		=> Describe(NavView.RepositoryDetail, 1, 1)
			.Should().Be("Fix will apply 1 auto-fix and triage 1 Dependabot pull request.");

	[Fact]
	public void WhereOneHalfHasNothingToDo_ItIsNotMentioned()
	{
		Describe(NavView.RepositoryDetail, 0, 6)
			.Should().Be("Fix will triage 6 Dependabot pull requests.");

		Describe(NavView.RepositoryDetail, 7, 0)
			.Should().Be("Fix will apply 7 auto-fixes.");
	}

	[Fact]
	public void OnTheInbox_ItNeverMentionsRemediations()
		=> Describe(NavView.RepositoryIssuesDetail, 7, 6)
			.Should().Be("Fix will triage 6 Dependabot pull requests.",
				"no failing rule sits beneath a pull request, so Fix would not apply them here");

	[Fact]
	public void OnARuleOrCategory_ItNeverMentionsTriage()
		=> Describe(NavView.RuleDetail, 7, 6)
			.Should().Be("Fix will apply 7 auto-fixes.");

	[Fact]
	public void WithNothingToDo_ItSaysSoRatherThanPromisingNothing()
		=> Describe(NavView.RepositoryDetail, 0, 0)
			.Should().Be("Fix has nothing to do here.");
}
