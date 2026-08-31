using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the two badges on the right of a repository node, and the guarantee that what they say no
/// longer leaks into the health glyph on the left.
/// </summary>
public class RepositoryStateBadgeTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static NavItem Node(
		bool cloned = true,
		bool dirty = false,
		bool? synced = true,
		RepositoryBuildState? build = null) => new()
		{
			Key = "repo:panoramicdata/Sample",
			Text = "Sample",
			IsClonedLocally = cloned,
			IsWorkingTreeDirty = dirty,
			IsSyncedWithOrigin = synced,
			BuildState = build
		};

	[Fact]
	public void NoCloneAtAll_OutranksEverythingElse()
		=> RepositoryStateBadges.GitIcon(Node(cloned: false, dirty: true, synced: false))
			.Should().Contain("fa-cloud").And.Contain("text-muted",
				"nothing on disk means the other two questions have no answer to give");

	[Fact]
	public void UncommittedWork_OutranksDriftFromOrigin()
		=> RepositoryStateBadges.GitIcon(Node(dirty: true, synced: false))
			.Should().Contain("fa-pen").And.Contain("text-warning",
				"what is not committed is the thing that is about to be lost or pushed");

	[Fact]
	public void DriftFromOrigin_IsWarned()
		=> RepositoryStateBadges.GitIcon(Node(synced: false)).Should().Contain("text-warning");

	[Fact]
	public void AnUnreadSyncState_IsGreyRatherThanGreen()
		=> RepositoryStateBadges.GitIcon(Node(synced: null))
			.Should().Contain("text-muted",
				"never having looked is not the same as having looked and found nothing wrong");

	[Fact]
	public void CleanAndInStep_IsGreen()
		=> RepositoryStateBadges.GitIcon(Node()).Should().Contain("text-success");

	[Theory]
	[InlineData(RepositoryBuildState.Succeeded, "text-success")]
	[InlineData(RepositoryBuildState.Failed, "text-danger")]
	[InlineData(null, "text-muted")]
	public void TheBuildBadge_ColoursTheLastResult(RepositoryBuildState? state, string expected)
		=> RepositoryStateBadges.BuildIcon(Node(build: state)).Should().Contain(expected);

	[Fact]
	public void TheBuildBadge_UsesOneGlyphThroughout()
	{
		var icons = new[] { RepositoryBuildState.Succeeded, RepositoryBuildState.Failed, (RepositoryBuildState?)null }
			.Select(state => RepositoryStateBadges.BuildIcon(Node(build: state)))
			.ToList();

		icons.Should().OnlyContain(icon => icon.Contains("fa-hammer", StringComparison.Ordinal),
			"one mark whose colour changes reads as one fact, where a changing glyph reads as three");
	}

	[Fact]
	public void NotKnowing_SaysWhyItMightNotBeKnown()
		=> RepositoryStateBadges.BuildTooltip(Node())
			.Should().Contain("changed since",
				"never built and built-then-changed are the same to a reader, and saying only 'never "
				+ "built' would be wrong half the time");

	[Theory]
	[InlineData(false, false, true)]
	[InlineData(true, true, false)]
	[InlineData(true, false, null)]
	public void TheHealthGlyph_NoLongerCarriesGitState(bool cloned, bool dirty, bool? synced)
	{
		var row = new RepositoryDashboardRow
		{
			RepositoryFullName = "panoramicdata/Sample",
			IsClonedLocally = cloned,
			IsWorkingTreeClean = !dirty,
			IsSyncedWithOrigin = synced
		};

		var plain = new RepositoryDashboardRow { RepositoryFullName = "panoramicdata/Sample" };

		NavTreeDataProvider.BuildRepositoryIconCss(row)
			.Should().Be(NavTreeDataProvider.BuildRepositoryIconCss(plain),
				"the glyph answers how the repository scores against the rules, and git state is not "
				+ "a rule");
	}
}
