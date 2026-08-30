namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// One open issue or pull request, as the issue API reports it.
/// </summary>
/// <param name="Number">The issue or pull request number.</param>
/// <param name="Title">The title.</param>
/// <param name="IsPullRequest">Whether the item is a pull request rather than an issue.</param>
/// <param name="HtmlUrl">The GitHub web address of the item.</param>
/// <param name="AuthorLogin">The login of whoever opened it.</param>
/// <param name="CreatedAtUtc">When it was opened.</param>
public record GitHubOpenItem(
	int Number,
	string Title,
	bool IsPullRequest,
	string HtmlUrl,
	string AuthorLogin,
	DateTimeOffset CreatedAtUtc);

/// <summary>
/// One comment, reduced to the three facts the staleness measure needs.
/// </summary>
/// <param name="IssueNumber">The issue or pull request the comment is on.</param>
/// <param name="CreatedAtUtc">When the comment was written.</param>
/// <param name="IsFromMaintainer">
/// Whether its author association was Owner, Member or Collaborator. Deciding this at the adapter
/// keeps GitHub's association vocabulary out of the sweep.
/// </param>
public record GitHubIssueComment(
	int IssueNumber,
	DateTimeOffset CreatedAtUtc,
	bool IsFromMaintainer);

/// <summary>
/// The narrow slice of the GitHub issue API this feature needs.
/// </summary>
/// <remarks>
/// A port rather than a direct dependency on <c>IGitHubClient</c>, which has hundreds of members and
/// cannot be implemented by hand — and this project has no mocking library. The same seam
/// <c>ICodacyIssueService</c> uses.
/// </remarks>
public interface IGitHubIssueApi
{
	/// <summary>
	/// Every open issue and pull request in a repository.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<IReadOnlyList<GitHubOpenItem>> GetOpenItemsAsync(
		string owner,
		string name,
		CancellationToken cancellationToken);

	/// <summary>
	/// One page of the repository's issue comments, newest first, 100 to a page.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="pageNumber">The one-based page number.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The page, or an empty list once the comments run out.</returns>
	Task<IReadOnlyList<GitHubIssueComment>> GetRepositoryCommentsPageAsync(
		string owner,
		string name,
		int pageNumber,
		CancellationToken cancellationToken);

	/// <summary>
	/// Every comment on one issue or pull request.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="issueNumber">The issue or pull request number.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<IReadOnlyList<GitHubIssueComment>> GetCommentsForItemAsync(
		string owner,
		string name,
		int issueNumber,
		CancellationToken cancellationToken);
}
