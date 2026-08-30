using Octokit;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// The Octokit-backed <see cref="IGitHubIssueApi"/>.
/// </summary>
/// <remarks>
/// Translation only. Every decision about what the data means — which associations count as a
/// maintainer, how far to sweep, what to do with what is left — belongs to
/// <see cref="RepositoryIssueService"/>, which can be tested. This class is kept thin enough that
/// there is nothing here to get wrong beyond the field names.
/// </remarks>
public class OctokitGitHubIssueApi(IGitHubClient github) : IGitHubIssueApi
{
	private const int PageSize = 100;

	private readonly IGitHubClient _github = github;

	/// <inheritdoc />
	public async Task<IReadOnlyList<GitHubOpenItem>> GetOpenItemsAsync(
		string owner,
		string name,
		CancellationToken cancellationToken)
	{
		// State.Open is the default, but saying it means the intent survives a future edit. The
		// endpoint returns pull requests alongside issues, which is what this feature wants.
		var request = new RepositoryIssueRequest { State = ItemStateFilter.Open };

		var issues = await _github.Issue
			.GetAllForRepository(owner, name, request, new ApiOptions { PageSize = PageSize })
			.ConfigureAwait(false);

		return [.. issues.Select(issue => new GitHubOpenItem(
			issue.Number,
			issue.Title ?? string.Empty,
			issue.PullRequest is not null,
			issue.HtmlUrl ?? string.Empty,
			issue.User?.Login ?? string.Empty,
			issue.CreatedAt))];
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<GitHubIssueComment>> GetRepositoryCommentsPageAsync(
		string owner,
		string name,
		int pageNumber,
		CancellationToken cancellationToken)
	{
		var request = new IssueCommentRequest
		{
			Sort = IssueCommentSort.Created,
			Direction = SortDirection.Descending
		};

		var options = new ApiOptions
		{
			PageSize = PageSize,
			PageCount = 1,
			StartPage = pageNumber
		};

		var comments = await _github.Issue.Comment
			.GetAllForRepository(owner, name, request, options)
			.ConfigureAwait(false);

		return [.. comments.Select(comment => Translate(comment))];
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<GitHubIssueComment>> GetCommentsForItemAsync(
		string owner,
		string name,
		int issueNumber,
		CancellationToken cancellationToken)
	{
		var comments = await _github.Issue.Comment
			.GetAllForIssue(owner, name, issueNumber, new ApiOptions { PageSize = PageSize })
			.ConfigureAwait(false);

		return [.. comments.Select(comment => Translate(comment, issueNumber))];
	}

	/// <summary>
	/// Whether an author association means the commenter has write access to the repository, and so
	/// that their comment counts as us having answered.
	/// </summary>
	/// <param name="association">The association GitHub reported on the comment.</param>
	/// <remarks>
	/// Public and static because it is the one judgement this otherwise mechanical class makes, and
	/// the only part of it worth a test.
	/// </remarks>
	public static bool IsMaintainerAssociation(AuthorAssociation association)
		=> association is AuthorAssociation.Owner
			or AuthorAssociation.Member
			or AuthorAssociation.Collaborator;

	/// <summary>
	/// Whether a comment's author association makes its writer a maintainer of the repository.
	/// </summary>
	private static bool IsMaintainer(IssueComment comment)
		=> IsMaintainerAssociation(comment.AuthorAssociation.Value);

	/// <summary>
	/// Translates a comment, taking its issue number from the URL GitHub returns on it.
	/// </summary>
	private static GitHubIssueComment Translate(IssueComment comment)
		=> new(IssueNumberFrom(comment.HtmlUrl), comment.CreatedAt, IsMaintainer(comment));

	/// <summary>
	/// Translates a comment whose issue number the caller already knows.
	/// </summary>
	private static GitHubIssueComment Translate(IssueComment comment, int issueNumber)
		=> new(issueNumber, comment.CreatedAt, IsMaintainer(comment));

	/// <summary>
	/// The issue number in a comment's web address, which ends "/issues/123#issuecomment-456" or
	/// "/pull/123#issuecomment-456". The repository-wide comment endpoint identifies the issue only
	/// by URL, so this is the only place the number can come from.
	/// </summary>
	private static int IssueNumberFrom(string? htmlUrl)
	{
		if (string.IsNullOrEmpty(htmlUrl))
		{
			return 0;
		}

		var withoutFragment = htmlUrl.Split('#')[0];
		var lastSegment = withoutFragment[(withoutFragment.LastIndexOf('/') + 1)..];

		return int.TryParse(lastSegment, out var number) ? number : 0;
	}
}
