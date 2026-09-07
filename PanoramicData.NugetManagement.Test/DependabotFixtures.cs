namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// The real bodies of the Dependabot pull requests standing open against
/// <c>panoramicdata/Highlight.Api</c>, captured verbatim.
/// </summary>
/// <remarks>
/// Committed rather than fetched at test time: the parser has to keep working against what Dependabot
/// actually writes, and a test that reached GitHub for it would pass or fail on network weather and
/// would silently start testing a different format the day those pull requests are closed.
/// <para>
/// Every one of these uses the same single form — <c>Updated [Name](url) from X to Y.</c> — for both
/// a single-dependency and a grouped pull request. That is the only reason the parser needs one
/// pattern rather than several, and it is a fact about Dependabot's output rather than a decision, so
/// these files are the authority when the pattern and the format disagree.
/// </para>
/// </remarks>
internal static class DependabotFixtures
{
	/// <summary>The pull request numbers a body was captured for.</summary>
	public static readonly int[] Numbers = [6, 26, 28, 30, 33];

	/// <summary>
	/// How many dependencies each captured pull request proposes moving, as its body lists them.
	/// </summary>
	/// <remarks>
	/// Stated here so a parser test can assert the count it should find without restating the body:
	/// #30 and #33 are the "and 2 others" pull requests whose titles name only the first of three.
	/// </remarks>
	public static readonly Dictionary<int, int> BumpCounts = new()
	{
		[6] = 1,
		[26] = 2,
		[28] = 2,
		[30] = 3,
		[33] = 3
	};

	/// <summary>
	/// The captured body of one pull request.
	/// </summary>
	/// <param name="pullRequestNumber">The pull request number, one of <see cref="Numbers"/>.</param>
	public static string Body(int pullRequestNumber)
	{
		var path = Path.Combine(
			AppContext.BaseDirectory,
			"Fixtures",
			"DependabotBodies",
			$"pr-{pullRequestNumber}.md");

		return File.Exists(path)
			? File.ReadAllText(path)
			: throw new FileNotFoundException(
				$"The captured body for pull request #{pullRequestNumber} is missing. It should have "
					+ "been committed with the tests; see "
					+ "docs/superpowers/plans/2026-09-03-dependabot-adoption.md Task 1.",
				path);
	}
}
