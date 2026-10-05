using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="RepositorySelection"/>: the set of repositories the toolbar will act on.
/// </summary>
public class RepositorySelectionTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void Toggle_TicksThenUnticks()
	{
		var selection = new RepositorySelection();

		selection.Toggle("panoramicdata/A").Should().BeTrue("the first toggle ticks it");
		selection.Contains("panoramicdata/A").Should().BeTrue();

		selection.Toggle("panoramicdata/A").Should().BeFalse("the second toggle unticks it");
		selection.Count.Should().Be(0);
	}

	[Fact]
	public void Names_AreMatchedWithoutRegardToCase()
	{
		// GitHub reports a repository's canonical casing, and a cached row may carry another. A
		// selection that treated them as different repositories would silently act on fewer than the
		// user ticked.
		var selection = new RepositorySelection();
		selection.Select("panoramicdata/Cisco.Iq.Api");

		selection.Contains("PanoramicData/cisco.iq.api").Should().BeTrue();

		selection.Toggle("PANORAMICDATA/CISCO.IQ.API").Should().BeFalse("it is the same repository");
		selection.Count.Should().Be(0);
	}

	[Fact]
	public void SelectAll_AndDeselectAll_ActOnlyOnTheNamesGiven()
	{
		var selection = new RepositorySelection();
		selection.Select("panoramicdata/Hidden");

		selection.SelectAll(["panoramicdata/A", "panoramicdata/B"]);
		selection.Count.Should().Be(3);

		selection.DeselectAll(["panoramicdata/A", "panoramicdata/B"]);

		selection.Names.Should().ContainSingle().Which.Should().Be("panoramicdata/Hidden",
			"unticking what is visible must leave a ticked row the filter is hiding alone");
	}

	[Fact]
	public void Prune_DropsNamesThatNoLongerExist_AndSaysHowMany()
	{
		var selection = new RepositorySelection();
		selection.SelectAll(["panoramicdata/A", "panoramicdata/B", "panoramicdata/C"]);

		var dropped = selection.Prune(["panoramicdata/A", "panoramicdata/c"]);

		dropped.Should().Be(1, "B has gone, and c is C in another case");
		selection.Count.Should().Be(2);
	}

	[Fact]
	public void Clear_EmptiesTheSelection()
	{
		var selection = new RepositorySelection();
		selection.Select("panoramicdata/A");

		selection.Clear();

		selection.Count.Should().Be(0);
	}

	[Theory]
	[InlineData(0, SelectionState.None)]
	[InlineData(1, SelectionState.Some)]
	[InlineData(3, SelectionState.All)]
	public void StateOf_ReportsHowMuchOfWhatIsVisibleIsTicked(int ticked, SelectionState expected)
	{
		var visible = new[] { "panoramicdata/A", "panoramicdata/B", "panoramicdata/C" };
		var selection = new RepositorySelection();
		selection.SelectAll(visible.Take(ticked));

		selection.StateOf(visible).Should().Be(expected);
	}

	[Fact]
	public void StateOf_NothingVisible_IsNone()
	{
		var selection = new RepositorySelection();
		selection.Select("panoramicdata/A");

		selection.StateOf([]).Should().Be(SelectionState.None,
			"with no visible rows there is nothing for a header checkbox to be ticked about");
	}
}
