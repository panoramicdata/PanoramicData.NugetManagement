using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="HumanIssueFilter"/>: which of the open items a person actually raised.
/// </summary>
/// <remarks>
/// Deliberately two rules rather than a list of known bots. The failure mode of a cleverer filter is
/// silently never analysing somebody's real report, which looks exactly like the feature working.
/// </remarks>
public class HumanIssueFilterTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryIssue Item(string author, bool isPullRequest = false) => new()
	{
		Number = 1,
		Title = "Missing methods",
		IsPullRequest = isPullRequest,
		HtmlUrl = "https://github.com/panoramicdata/OpenProject.Api/issues/1",
		AuthorLogin = author,
		CreatedAtUtc = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero)
	};

	[Fact]
	public void IsHumanRaised_AcceptsAnIssueFromAPerson()
		=> HumanIssueFilter.IsHumanRaised(Item("ismail-ozturk")).Should().BeTrue();

	[Fact]
	public void IsHumanRaised_RejectsAPullRequest()
		=> HumanIssueFilter.IsHumanRaised(Item("ismail-ozturk", isPullRequest: true)).Should().BeFalse(
			"a pull request carries a diff to read, which is a different question from reading prose");

	[Fact]
	public void IsHumanRaised_RejectsABot()
		=> HumanIssueFilter.IsHumanRaised(Item("dependabot[bot]")).Should().BeFalse(
			"Dependabot triage already owns these, and analysing them twice would have the two "
				+ "passes disagreeing in public");

	[Fact]
	public void IsHumanRaised_AcceptsSomeoneWhoseNameMerelyMentionsABot()
		=> HumanIssueFilter.IsHumanRaised(Item("robotics-fan")).Should().BeTrue(
			"the suffix is GitHub's own marker; matching on the word anywhere would drop real people");
}
