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
		{
			FakeArguments.Accept(owner, name, cancellationToken);
			return Task.FromResult(items);
		}

		public Task<IReadOnlyList<GitHubIssueComment>> GetRepositoryCommentsPageAsync(
			string owner, string name, int pageNumber, CancellationToken cancellationToken)
		{
			FakeArguments.Accept(owner, name, cancellationToken);
			PagesRequested = Math.Max(PagesRequested, pageNumber);
			return Task.FromResult(pageNumber <= pages.Count ? pages[pageNumber - 1] : []);
		}

		public Task<IReadOnlyList<GitHubIssueComment>> GetCommentsForItemAsync(
			string owner, string name, int issueNumber, CancellationToken cancellationToken)
		{
			FakeArguments.Accept(owner, name, cancellationToken);
			ItemsFetchedIndividually.Add(issueNumber);
			return Task.FromResult(
				perItem is not null && perItem.TryGetValue(issueNumber, out var found) ? found : []);
		}

		public Task<GitHubIssueThread> GetThreadAsync(
			string owner, string name, int issueNumber, CancellationToken cancellationToken)
		{
			FakeArguments.Accept(owner, name, issueNumber, cancellationToken);
			throw new NotSupportedException(
					"The staleness sweep reads timestamps, never prose. Fetching a thread here would be "
						+ "the cost this port was split to avoid.");
		}
	}

	private static GitHubIssueComment Comment(int issueNumber, TimeSpan ago, bool maintainer)
		=> new(issueNumber, Now - ago, maintainer);

	[Fact]
	public async Task AnUnattributableCommentFallsBackToAskingItemByItem()
	{
		// Issue number 0 is what the adapter yields for a comment URL it cannot parse. The sweep must
		// not treat the short page as proof that nobody answered item 1.
		var api = new FakeApi(
			[Item(1)],
			[[Comment(0, TimeSpan.FromDays(1), maintainer: true)]],
			new Dictionary<int, IReadOnlyList<GitHubIssueComment>>
			{
				[1] = [Comment(1, TimeSpan.FromDays(1), maintainer: true)]
			});
		var service = new RepositoryIssueService(api);

		var result = await service.GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		api.ItemsFetchedIndividually.Should().Equal(1);
		result.Single().LastMaintainerReplyUtc.Should().Be(Now - TimeSpan.FromDays(1));
	}

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
			.Select(_ => (IReadOnlyList<GitHubIssueComment>)[.. Enumerable.Repeat(Comment(999, TimeSpan.FromDays(1), maintainer: true), IGitHubIssueApi.CommentPageSize)])
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
	public async Task AShortNonEmptyPageEndsTheSweepWithoutAnExtraRequest()
	{
		var api = new FakeApi([Item(1)], [[Comment(99, TimeSpan.FromDays(1), maintainer: true)]]);

		await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		api.PagesRequested.Should().Be(1, "a page shorter than the page size is the last page");
		api.ItemsFetchedIndividually.Should().BeEmpty();
	}

	[Fact]
	public async Task AnExhaustedSweepAnswersAnUnansweredItemWithoutAskingAboutIt()
	{
		// One open item nobody has answered, and a first page that comes back empty: the sweep has
		// now seen every comment the repository has.
		var api = new FakeApi([Item(1)], [[]]);

		var result = await new RepositoryIssueService(api).GetOpenIssuesAsync(
			"panoramicdata", "Sample", TestContext.Current.CancellationToken);

		api.ItemsFetchedIndividually.Should().BeEmpty(
			"the sweep read every comment in the repository and none was a maintainer reply to this "
			+ "item, so asking about it individually could only re-learn what is already known — and "
			+ "unanswered items are the common case this feature exists to surface");
		result.Single().LastMaintainerReplyUtc.Should().BeNull();
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
		api.ItemsFetchedIndividually.Should().BeEmpty(
			"page two comes back empty, so the sweep has seen every comment and knows item 1 is unanswered");
	}
}
