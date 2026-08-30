using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests the comment sweep of <see cref="RepositoryIssueService"/> against a fake GitHub API.
/// </summary>
public class RepositoryIssueServiceTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

	private static GitHubOpenItem Item(int number, bool isPullRequest = false)
		=> new(
			number,
			$"Item {number}",
			isPullRequest,
			$"https://github.com/panoramicdata/Sample/issues/{number}",
			"reporter",
			Now - TimeSpan.FromDays(200));

	/// <summary>
	/// A fake API returning fixed open items and fixed pages of comments, counting what was asked
	/// for so the tests can assert on the cost of the sweep.
	/// </summary>
	private sealed class FakeApi(
		IReadOnlyList<GitHubOpenItem> items,
		IReadOnlyList<IReadOnlyList<GitHubIssueComment>> pages,
		IReadOnlyDictionary<int, IReadOnlyList<GitHubIssueComment>>? perItem = null)
		: IGitHubIssueApi
	{
		public int PagesRequested { get; private set; }

		public List<int> ItemsFetchedIndividually { get; } = [];

		public Task<IReadOnlyList<GitHubOpenItem>> GetOpenItemsAsync(
			string owner, string name, CancellationToken cancellationToken)
			=> Task.FromResult(items);

		public Task<IReadOnlyList<GitHubIssueComment>> GetRepositoryCommentsPageAsync(
			string owner, string name, int pageNumber, CancellationToken cancellationToken)
		{
			PagesRequested = Math.Max(PagesRequested, pageNumber);
			return Task.FromResult(pageNumber <= pages.Count ? pages[pageNumber - 1] : []);
		}

		public Task<IReadOnlyList<GitHubIssueComment>> GetCommentsForItemAsync(
			string owner, string name, int issueNumber, CancellationToken cancellationToken)
		{
			ItemsFetchedIndividually.Add(issueNumber);
			return Task.FromResult(
				perItem is not null && perItem.TryGetValue(issueNumber, out var found) ? found : []);
		}
	}

	private static GitHubIssueComment Comment(int issueNumber, TimeSpan ago, bool maintainer)
		=> new(issueNumber, Now - ago, maintainer);

	[Fact]
	public async Task OpenIssuesAndPullRequestsBothAppearWithTheirKind()
	{
		var api = new FakeApi([Item(1), Item(2, isPullRequest: true)], [[]]);
		var service = new RepositoryIssueService(api);

		var result = await service.GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		result.Should().HaveCount(2);
		result.Single(i => i.Number == 1).IsPullRequest.Should().BeFalse();
		result.Single(i => i.Number == 2).IsPullRequest.Should().BeTrue();
	}

	[Fact]
	public async Task TheNewestMaintainerCommentIsTheLastReply()
	{
		var api = new FakeApi(
			[Item(1)],
			[[
				Comment(1, TimeSpan.FromDays(2), maintainer: true),
				Comment(1, TimeSpan.FromDays(9), maintainer: true)
			]]);

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		result.Single().LastMaintainerReplyUtc.Should().Be(Now - TimeSpan.FromDays(2));
	}

	[Fact]
	public async Task ACommentFromTheReporterDoesNotCountAsAReply()
	{
		var api = new FakeApi(
			[Item(1)],
			[[
				Comment(1, TimeSpan.FromDays(1), maintainer: false),
				Comment(1, TimeSpan.FromDays(60), maintainer: true)
			]]);

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		result.Single().LastMaintainerReplyUtc.Should().Be(Now - TimeSpan.FromDays(60));
	}

	[Fact]
	public async Task TheSweepStopsAsSoonAsEveryItemIsResolved()
	{
		var api = new FakeApi(
			[Item(1)],
			[
				[Comment(1, TimeSpan.FromDays(1), maintainer: true)],
				[Comment(1, TimeSpan.FromDays(50), maintainer: true)]
			]);

		await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		api.PagesRequested.Should().Be(1, "every open item was resolved by the first page");
	}

	[Fact]
	public async Task TheSweepStopsAtThePageBudgetAndFallsBackPerItem()
	{
		// Full pages of comments on an unrelated issue, so the sweep never sees a short page that
		// would signal the comments have run out; it must exhaust the page budget instead.
		var unrelatedPages = Enumerable.Range(0, 20)
			.Select(_ => (IReadOnlyList<GitHubIssueComment>)[Comment(999, TimeSpan.FromDays(1), maintainer: true)])
			.ToList();

		var api = new FakeApi(
			[Item(1)],
			unrelatedPages,
			new Dictionary<int, IReadOnlyList<GitHubIssueComment>>
			{
				[1] = [Comment(1, TimeSpan.FromDays(3), maintainer: true)]
			});

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		api.PagesRequested.Should().Be(RepositoryIssueService.MaxSweepPages);
		api.ItemsFetchedIndividually.Should().Equal(1);
		result.Single().LastMaintainerReplyUtc.Should().Be(Now - TimeSpan.FromDays(3));
	}

	[Fact]
	public async Task AnItemNoMaintainerEverAnsweredHasNoReplyTime()
	{
		var api = new FakeApi([Item(1)], [[Comment(1, TimeSpan.FromDays(2), maintainer: false)]]);

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		result.Single().LastMaintainerReplyUtc.Should().BeNull();
	}

	[Fact]
	public async Task AnExhaustedSweepStopsWithoutHittingTheBudget()
	{
		var api = new FakeApi([Item(1)], [[]]);

		await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		api.PagesRequested.Should().Be(1, "a short page means the comments ran out");
	}

	[Fact]
	public async Task CommentsOnOtherItemsAreIgnored()
	{
		var api = new FakeApi(
			[Item(1)],
			[[Comment(99, TimeSpan.FromDays(1), maintainer: true)]]);

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		result.Single().LastMaintainerReplyUtc.Should().BeNull();
	}
}
