using System.Text.Json;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="RepositoryIssue.Body"/>: triage needs it within the pass that fetched it, and
/// the row cache must not carry it.
/// </summary>
public class RepositoryIssueBodyTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryIssue Issue(string? body) => new()
	{
		Number = 30,
		Title = "Bump Microsoft.Extensions.DependencyInjection and 2 others",
		IsPullRequest = true,
		HtmlUrl = "https://github.com/panoramicdata/Highlight.Api/pull/30",
		AuthorLogin = "dependabot[bot]",
		CreatedAtUtc = new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero),
		Body = body
	};

	[Fact]
	public void Body_WhenSet_IsReadable()
		=> Issue("Updated [Something](https://example.com) from 1.0.0 to 2.0.0.")
			.Body.Should().Be("Updated [Something](https://example.com) from 1.0.0 to 2.0.0.");

	[Fact]
	public void Body_WhenNotSet_IsNull()
		=> Issue(null).Body.Should().BeNull(
			"a row restored from the cache has no body, and that has to be distinguishable from an "
				+ "empty one");

	[Fact]
	public void Body_IsNotPersisted()
		=> JsonSerializer
			.Serialize(Issue("a body long enough to notice in a cache file"))
			.Should().NotContain(
				"long enough to notice",
				"Dependabot bodies carry whole changelogs, and the row cache holds every open item of "
					+ "every repository — persisting them would inflate it to store text only ever read "
					+ "during the pass that fetched it");

	[Fact]
	public void Body_SurvivesNothingAcrossARoundTrip()
	{
		var restored = JsonSerializer.Deserialize<RepositoryIssue>(
			JsonSerializer.Serialize(Issue("Updated [Something](https://example.com) from 1 to 2.")));

		restored.Should().NotBeNull();
		restored!.Title.Should().Be(
			"Bump Microsoft.Extensions.DependencyInjection and 2 others",
			"everything else about the item still round-trips");
		restored.Body.Should().BeNull("the body is deliberately left behind");
	}
}
