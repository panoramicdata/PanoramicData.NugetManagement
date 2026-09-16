using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the pieces behind <see cref="ICodacySecurityService"/>: how a Codacy SRM item becomes
/// a finding, how a multi-page search is drained, and how three rules asking the same question in
/// one assessment produce one call rather than three.
/// </summary>
public class CodacySecurityServiceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static SrmItem Item(
		SrmPriority priority = SrmPriority.Critical,
		SrmStatus status = SrmStatus.Overdue,
		string? securityCategory = "CommandInjection",
		string? scanType = "SAST")
		=> new()
		{
			Id = Guid.Parse("9bbae4d1-da30-43c0-a243-6e8add96ae8c"),
			ItemSource = SrmSource.Codacy,
			ItemSourceId = "131466833554",
			Title = "OS command injection is a critical vulnerability.",
			Repository = "Sample.Api",
			OpenedAt = new DateTimeOffset(2026, 4, 3, 14, 16, 17, TimeSpan.Zero),
			DueAt = new DateTimeOffset(2026, 5, 3, 14, 16, 17, TimeSpan.Zero),
			Priority = priority,
			Status = status,
			SecurityCategory = securityCategory,
			ScanType = scanType,
			HtmlUrl = "https://app.codacy.com/p/848725/issues/index?resultDataId=131466833554"
		};

	[Theory]
	[InlineData(SrmPriority.Critical, CodacySecurityPriority.Critical)]
	[InlineData(SrmPriority.High, CodacySecurityPriority.High)]
	[InlineData(SrmPriority.Medium, CodacySecurityPriority.Medium)]
	[InlineData(SrmPriority.Low, CodacySecurityPriority.Low)]
	public void Map_CarriesThePriorityTheRagGradingDependsOn(SrmPriority priority, CodacySecurityPriority expected)
		=> CodacySecurityMapper.Map(Item(priority)).Priority.Should().Be(expected);

	[Fact]
	public void Map_CarriesTheFieldsTheAdvisoryShows()
	{
		var finding = CodacySecurityMapper.Map(Item());

		finding.Id.Should().Be(Guid.Parse("9bbae4d1-da30-43c0-a243-6e8add96ae8c"));
		finding.Title.Should().Be("OS command injection is a critical vulnerability.");
		finding.SecurityCategory.Should().Be("CommandInjection");
		finding.ScanType.Should().Be("SAST");
		finding.SlaStatus.Should().Be(CodacySecuritySlaStatus.Overdue);
		finding.HtmlUrl.Should().Be("https://app.codacy.com/p/848725/issues/index?resultDataId=131466833554");
		finding.OpenedAt.Should().Be(new DateTimeOffset(2026, 4, 3, 14, 16, 17, TimeSpan.Zero));
		finding.DueAt.Should().Be(new DateTimeOffset(2026, 5, 3, 14, 16, 17, TimeSpan.Zero));
	}

	[Theory]
	[InlineData(SrmStatus.OnTrack, CodacySecuritySlaStatus.OnTrack)]
	[InlineData(SrmStatus.DueSoon, CodacySecuritySlaStatus.DueSoon)]
	[InlineData(SrmStatus.Overdue, CodacySecuritySlaStatus.Overdue)]
	public void Map_CarriesTheSlaStatusTheAdvisoryGroupsBy(SrmStatus status, CodacySecuritySlaStatus expected)
		=> CodacySecurityMapper.Map(Item(status: status)).SlaStatus.Should().Be(expected);

	[Fact]
	public void Map_RefusesAnSlaStatusThisBuildCannotGroup()
	{
		var act = () => CodacySecurityMapper.Map(Item(status: (SrmStatus)999));

		act.Should().Throw<ArgumentOutOfRangeException>(
			"a status silently grouped as on track would hide an overdue finding");
	}

	[Fact]
	public void Map_LeavesAnUncategorisedFindingUncategorised()
	{
		var finding = CodacySecurityMapper.Map(Item(securityCategory: null, scanType: null));

		finding.SecurityCategory.Should().BeNull();
		finding.ScanType.Should().BeNull();
	}

	[Fact]
	public async Task DrainAsync_FollowsTheCursorUntilItRunsOut()
	{
		var pages = new Queue<(List<SrmItem> Items, string? Cursor)>(
		[
			([Item(), Item()], "2"),
			([Item()], "3"),
			([], null)
		]);
		var cursorsAsked = new List<string?>();

		var items = await CodacySecurityPager.DrainAsync(
			(cursor, _) =>
			{
				cursorsAsked.Add(cursor);
				var page = pages.Dequeue();
				return Task.FromResult<(IReadOnlyList<SrmItem>, string?)>((page.Items, page.Cursor));
			},
			TestContext.Current.CancellationToken);

		items.Should().HaveCount(3);
		cursorsAsked.Should().Equal([null, "2", "3"]);
	}

	[Fact]
	public async Task DrainAsync_StopsOnTheFirstPageWhenThereIsNoCursor()
	{
		var calls = 0;

		var items = await CodacySecurityPager.DrainAsync(
			(_, _) =>
			{
				calls++;
				return Task.FromResult<(IReadOnlyList<SrmItem>, string?)>(([Item()], null));
			},
			TestContext.Current.CancellationToken);

		items.Should().ContainSingle();
		calls.Should().Be(1);
	}

	[Fact]
	public async Task DrainAsync_StopsWhenAPageRepeatsItsCursor()
	{
		// A cursor that never advances would otherwise page forever against a live API.
		var calls = 0;

		var items = await CodacySecurityPager.DrainAsync(
			(_, _) =>
			{
				calls++;
				return Task.FromResult<(IReadOnlyList<SrmItem>, string?)>(([Item()], "stuck"));
			},
			TestContext.Current.CancellationToken);

		calls.Should().Be(2, "the second page repeated the cursor the first one returned");
		items.Should().HaveCount(2);
	}

	[Fact]
	public async Task Memo_AnswersThreeRulesWithOneFetch()
	{
		var memo = new CodacySecurityMemo(TimeSpan.FromMinutes(2));
		var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
		var fetches = 0;

		Task<CodacySecurityReport> Fetch()
		{
			fetches++;
			return Task.FromResult(new CodacySecurityReport { IsTracked = true, Findings = [] });
		}

		for (var i = 0; i < 3; i++)
		{
			_ = await memo.GetOrAddAsync("panoramicdata/Sample.Api", now, Fetch);
		}

		fetches.Should().Be(1);
	}

	[Fact]
	public async Task Memo_FetchesAgainForADifferentRepository()
	{
		var memo = new CodacySecurityMemo(TimeSpan.FromMinutes(2));
		var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
		var fetches = 0;

		Task<CodacySecurityReport> Fetch()
		{
			fetches++;
			return Task.FromResult(new CodacySecurityReport { IsTracked = true, Findings = [] });
		}

		_ = await memo.GetOrAddAsync("panoramicdata/A", now, Fetch);
		_ = await memo.GetOrAddAsync("panoramicdata/B", now, Fetch);

		fetches.Should().Be(2);
	}

	[Fact]
	public async Task Memo_FetchesAgainOnceTheEntryHasExpired()
	{
		// A later assessment must see Codacy's current answer, not the one the last one cached.
		var memo = new CodacySecurityMemo(TimeSpan.FromMinutes(2));
		var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
		var fetches = 0;

		Task<CodacySecurityReport> Fetch()
		{
			fetches++;
			return Task.FromResult(new CodacySecurityReport { IsTracked = true, Findings = [] });
		}

		_ = await memo.GetOrAddAsync("panoramicdata/Sample.Api", now, Fetch);
		_ = await memo.GetOrAddAsync("panoramicdata/Sample.Api", now.AddMinutes(3), Fetch);

		fetches.Should().Be(2);
	}

	[Fact]
	public async Task Memo_DoesNotCacheAFailedFetch()
	{
		// Caching a transient failure would silence the security rules for the whole window.
		var memo = new CodacySecurityMemo(TimeSpan.FromMinutes(2));
		var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
		var fetches = 0;

		Task<CodacySecurityReport> Failing()
		{
			fetches++;
			throw new InvalidOperationException("Codacy unreachable");
		}

		var first = async () => await memo.GetOrAddAsync("panoramicdata/Sample.Api", now, Failing).ConfigureAwait(false);
		await first.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);

		var second = async () => await memo.GetOrAddAsync("panoramicdata/Sample.Api", now, Failing).ConfigureAwait(false);
		await second.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);

		fetches.Should().Be(2);
	}

	[Fact]
	public void OpenStatuses_AreTheThreeCodacyCountsAsOpen()
		=> CodacySecurityService.OpenStatuses.Should()
			.BeEquivalentTo([SrmStatus.OnTrack, SrmStatus.DueSoon, SrmStatus.Overdue]);
}
