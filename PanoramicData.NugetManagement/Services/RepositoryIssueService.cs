using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Builds the list of open issues and pull requests for a repository, each carrying the time a
/// maintainer last commented on it.
/// </summary>
/// <remarks>
/// The naive implementation fetches every item's comments, costing one request per open item. A
/// repository with a Dependabot backlog would spend a large share of a 5,000/hour budget on a single
/// refresh. So the repository's comments are swept newest-first instead: walked in that order, the
/// first maintainer comment seen for an item is that item's last maintainer reply, and the walk
/// stops as soon as every open item is answered. Repositories whose recent conversation is mostly on
/// currently-open items — the normal case — cost one or two pages.
/// <para>
/// Where the sweep runs out of comments before the page budget, it has seen every comment in the
/// repository, so every item still unanswered is definitively unanswered and needs no further
/// request. Only a sweep stopped by the budget falls back to asking item by item.
/// </para>
/// </remarks>
public class RepositoryIssueService(IGitHubIssueApi api)
{
	/// <summary>
	/// How many pages of repository comments the sweep will read before giving up and asking about
	/// the remaining items one at a time. Bounds the cost of a repository with thousands of comments
	/// on long-closed issues, without making any single answer less exact.
	/// </summary>
	public const int MaxSweepPages = 5;

	private readonly IGitHubIssueApi _api = api;

	/// <summary>
	/// The open issues and pull requests of a repository.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<IReadOnlyList<RepositoryIssue>> GetOpenIssuesAsync(
		string owner,
		string name,
		CancellationToken cancellationToken)
	{
		var items = await _api
			.GetOpenItemsAsync(owner, name, cancellationToken)
			.ConfigureAwait(false);

		if (items.Count == 0)
		{
			return [];
		}

		var replies = new Dictionary<int, DateTimeOffset>();
		var unresolved = items.Select(item => item.Number).ToHashSet();

		// Whether the sweep read every comment the repository has, rather than stopping at the page
		// budget. It decides whether the per-item fallback is needed at all.
		var sweptEveryComment = false;

		for (var page = 1; page <= MaxSweepPages && unresolved.Count > 0; page++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var comments = await _api
				.GetRepositoryCommentsPageAsync(owner, name, page, cancellationToken)
				.ConfigureAwait(false);

			// An empty page means the comments ran out. Everything still unresolved at this point has
			// provably never been answered by a maintainer, because the sweep has now seen every
			// comment in the repository and none of them was one.
			if (comments.Count == 0)
			{
				sweptEveryComment = true;
				break;
			}

			foreach (var comment in comments)
			{
				if (!comment.IsFromMaintainer || !unresolved.Contains(comment.IssueNumber))
				{
					continue;
				}

				// Newest-first, so the first maintainer comment seen for an item is its latest.
				replies[comment.IssueNumber] = comment.CreatedAtUtc;
				unresolved.Remove(comment.IssueNumber);
			}
		}

		// Only what the BUDGET could not reach is asked about directly, so that an item whose last
		// maintainer comment lies beyond the swept pages still gets an exact answer rather than being
		// reported as never answered.
		//
		// This is skipped entirely once the sweep has exhausted the comments, and that is the whole
		// point: the sweep can only ever RESOLVE an item by finding a maintainer comment for it, so an
		// unanswered item is unresolvable by construction and used to fall through to a per-item
		// request every time. Unanswered items are exactly what this feature exists to surface, which
		// made the common case the expensive one — 200 unanswered Dependabot pull requests cost 200
		// extra requests per refresh, to re-learn what the sweep already knew.
		if (!sweptEveryComment)
		{
			foreach (var number in items.Select(i => i.Number).Where(n => !replies.ContainsKey(n)))
			{
				cancellationToken.ThrowIfCancellationRequested();

				var comments = await _api
					.GetCommentsForItemAsync(owner, name, number, cancellationToken)
					.ConfigureAwait(false);

				var latest = comments
					.Where(c => c.IsFromMaintainer)
					.Select(c => (DateTimeOffset?)c.CreatedAtUtc)
					.DefaultIfEmpty(null)
					.Max();

				if (latest is not null)
				{
					replies[number] = latest.Value;
				}
			}
		}

		return [.. items.Select(item => new RepositoryIssue
		{
			Number = item.Number,
			Title = item.Title,
			IsPullRequest = item.IsPullRequest,
			HtmlUrl = item.HtmlUrl,
			AuthorLogin = item.AuthorLogin,
			CreatedAtUtc = item.CreatedAtUtc,
			LastMaintainerReplyUtc = replies.TryGetValue(item.Number, out var reply) ? reply : null
		})];
	}
}
