using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the staleness bands of <see cref="RepositoryIssue"/>.
/// </summary>
public class RepositoryIssueSeverityTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

	private static RepositoryIssue Replied(TimeSpan ago)
		=> new()
		{
			Number = 1,
			Title = "Something",
			HtmlUrl = "https://github.com/panoramicdata/Sample/issues/1",
			AuthorLogin = "reporter",
			CreatedAtUtc = Now - TimeSpan.FromDays(365),
			LastMaintainerReplyUtc = Now - ago
		};

	[Fact]
	public void AReplyMinutesAgoIsInformational()
		=> Replied(TimeSpan.FromMinutes(5)).SeverityAt(Now).Should().Be(AssessmentSeverity.Info);

	[Fact]
	public void AMomentUnderSevenDaysIsStillInformational()
		=> Replied(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1)).SeverityAt(Now)
			.Should().Be(AssessmentSeverity.Info);

	[Fact]
	public void ExactlySevenDaysIsAnError()
		=> Replied(TimeSpan.FromDays(7)).SeverityAt(Now).Should().Be(AssessmentSeverity.Error);

	[Fact]
	public void AMomentUnderThirtyDaysIsStillAnError()
		=> Replied(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1)).SeverityAt(Now)
			.Should().Be(AssessmentSeverity.Error);

	[Fact]
	public void ExactlyThirtyDaysIsCritical()
		=> Replied(TimeSpan.FromDays(30)).SeverityAt(Now).Should().Be(AssessmentSeverity.Critical);

	[Fact]
	public void NoMaintainerReplyEverBandsOnTheCreationDate()
	{
		var issue = new RepositoryIssue
		{
			Number = 2,
			Title = "Never answered",
			HtmlUrl = "https://github.com/panoramicdata/Sample/issues/2",
			AuthorLogin = "reporter",
			CreatedAtUtc = Now - TimeSpan.FromDays(31),
			LastMaintainerReplyUtc = null
		};

		issue.ClockStartUtc.Should().Be(issue.CreatedAtUtc);
		issue.SeverityAt(Now).Should().Be(AssessmentSeverity.Critical);
	}

	[Fact]
	public void ABotAuthoredItemBandsExactlyAsAHumanOneDoes()
	{
		var bot = new RepositoryIssue
		{
			Number = 3,
			Title = "Bump Newtonsoft.Json from 13.0.3 to 13.0.4",
			IsPullRequest = true,
			HtmlUrl = "https://github.com/panoramicdata/Sample/pull/3",
			AuthorLogin = "dependabot[bot]",
			CreatedAtUtc = Now - TimeSpan.FromDays(40),
			LastMaintainerReplyUtc = null
		};

		bot.SeverityAt(Now).Should().Be(AssessmentSeverity.Critical);
	}

	[Fact]
	public void TheBandsNeverReturnWarning()
	{
		var days = Enumerable.Range(0, 120).Select(d => Replied(TimeSpan.FromDays(d)));
		days.Should().AllSatisfy(i => i.SeverityAt(Now).Should().NotBe(AssessmentSeverity.Warning));
	}
}
