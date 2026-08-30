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

		for (var page = 1; page <= MaxSweepPages && unresolved.Count > 0; page++)
		{
			var comments = await _api
				.GetRepositoryCommentsPageAsync(owner, name, page, cancellationToken)
				.ConfigureAwait(false);

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

			// A short page means the comments ran out; there is nothing further to ask for.
			if (comments.Count == 0)
			{
				unresolved.Clear();
				break;
			}
		}

		// Anything the budget could not reach is asked about directly, so that an item whose last
		// maintainer comment lies beyond the swept pages still gets an exact answer rather than
		// being reported as never answered.
		foreach (var number in items.Select(i => i.Number).Where(n => !replies.ContainsKey(n)))
		{
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
