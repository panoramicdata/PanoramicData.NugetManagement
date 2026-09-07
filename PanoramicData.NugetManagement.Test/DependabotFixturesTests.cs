using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DependabotFixtures"/>: that the captured bodies reached the output directory
/// and still say what the parser was built against.
/// </summary>
/// <remarks>
/// These pin the <em>format</em>, separately from any test of the parser. A parser test failing tells
/// you something is wrong; these tell you whether it is the parser or Dependabot's wording that
/// changed, which is the difference between a bug and a re-capture.
/// <para>
/// The <c>\r?</c> before each anchor is load-bearing. These files are committed with LF and checked
/// out with CRLF on Windows, so the line endings differ by platform and by clone — and <c>$</c> in
/// .NET's multiline mode matches only before <c>\n</c>, leaving a stray <c>\r</c> to break an
/// otherwise correct pattern.
/// </para>
/// </remarks>
public partial class DependabotFixturesTests(ITestOutputHelper output) : TestWithOutput(output)
{
	/// <summary>The start of one dependency-move line, as Dependabot writes it.</summary>
	[GeneratedRegex(@"^Updated \[", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex MoveLine();

	[Fact]
	public void Body_ForEveryCapturedPullRequest_IsNotEmpty()
	{
		foreach (var number in DependabotFixtures.Numbers)
		{
			DependabotFixtures
				.Body(number)
				.Should().NotBeNullOrWhiteSpace($"pull request #{number}'s body was captured in Task 1");
		}
	}

	[Fact]
	public void Body_ForEveryCapturedPullRequest_StatesEachMoveInTheOneKnownForm()
	{
		foreach (var number in DependabotFixtures.Numbers)
		{
			DependabotFixtures
				.Body(number)
				.Should().MatchRegex(
					@"(?m)^Updated \[[^\]]+\]\([^)]*\) from \S+ to \S+\r?$",
					$"pull request #{number} states its moves as 'Updated [Name](url) from X to Y.', "
						+ "which is the single form the parser reads");
		}
	}

	[Fact]
	public void Body_LineCount_MatchesWhatEachPullRequestProposes()
	{
		foreach (var (number, expected) in DependabotFixtures.BumpCounts)
		{
			MoveLine()
				.Matches(DependabotFixtures.Body(number))
				.Count.Should().Be(
					expected,
					$"pull request #{number} proposes {expected} move(s), and a grouped title names only "
						+ "the first of them — the body is the only place the rest appear");
		}
	}
}
