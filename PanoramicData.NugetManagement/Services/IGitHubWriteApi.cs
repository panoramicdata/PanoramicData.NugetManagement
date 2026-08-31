namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// The narrow slice of GitHub this application writes to.
/// </summary>
/// <remarks>
/// Separate from <see cref="IGitHubIssueApi"/> on purpose. Everything else this application does to
/// GitHub is read-only, and keeping the two apart means the read path stays provably so — a test
/// double standing in for the staleness sweep cannot accidentally gain the ability to close somebody's
/// pull request. A consumer that needs both takes both, and says so in its constructor.
/// </remarks>
public interface IGitHubWriteApi
{
	/// <summary>
	/// Opens an issue.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="title">The issue title.</param>
	/// <param name="body">The issue body, in markdown.</param>
	/// <param name="labels">Labels to apply; may be empty.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The new issue's number.</returns>
	Task<int> CreateIssueAsync(
		string owner,
		string name,
		string title,
		string body,
		IReadOnlyList<string> labels,
		CancellationToken cancellationToken);

	/// <summary>
	/// Replaces an issue's body.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="number">The issue number.</param>
	/// <param name="body">The replacement body, in markdown.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task UpdateIssueBodyAsync(
		string owner,
		string name,
		int number,
		string body,
		CancellationToken cancellationToken);

	/// <summary>
	/// Adds a comment to an issue or pull request.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="number">The issue or pull request number.</param>
	/// <param name="body">The comment body, in markdown.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task CommentAsync(
		string owner,
		string name,
		int number,
		string body,
		CancellationToken cancellationToken);

	/// <summary>
	/// Closes an issue.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="number">The issue number.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <remarks>
	/// Separate from <see cref="ClosePullRequestAsync"/> even though GitHub models both as issues,
	/// because the two are different decisions with different blast radii: one ends somebody's pull
	/// request, the other retracts an issue this application raised itself.
	/// </remarks>
	Task CloseIssueAsync(
		string owner,
		string name,
		int number,
		CancellationToken cancellationToken);

	/// <summary>
	/// Closes a pull request, leaving its branch alone.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="number">The pull request number.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <remarks>
	/// No <c>@dependabot ignore</c> directive accompanies this, and none should. The only pull requests
	/// this application closes are ones whose manifest already meets or exceeds the target, so
	/// Dependabot has no update left to propose and will not recreate them. An ignore directive would
	/// suppress a future legitimate bump for no gain.
	/// </remarks>
	Task ClosePullRequestAsync(
		string owner,
		string name,
		int number,
		CancellationToken cancellationToken);
}
