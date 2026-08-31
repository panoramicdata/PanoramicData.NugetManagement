using Octokit;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// The Octokit-backed <see cref="IGitHubWriteApi"/>.
/// </summary>
/// <remarks>
/// Translation only, as <see cref="OctokitGitHubIssueApi"/> is. Every decision about whether a pull
/// request should be closed at all belongs to <see cref="DependabotTriageService"/>, which can be
/// tested without a network.
/// </remarks>
public class OctokitGitHubWriteApi(IGitHubClient github) : IGitHubWriteApi
{
	private readonly IGitHubClient _github = github;

	/// <inheritdoc />
	public async Task<int> CreateIssueAsync(
		string owner,
		string name,
		string title,
		string body,
		IReadOnlyList<string> labels,
		CancellationToken cancellationToken)
	{
		var request = new NewIssue(title) { Body = body };

		foreach (var label in labels)
		{
			request.Labels.Add(label);
		}

		var created = await _github.Issue.Create(owner, name, request).ConfigureAwait(false);

		return created.Number;
	}

	/// <inheritdoc />
	public async Task UpdateIssueBodyAsync(
		string owner,
		string name,
		int number,
		string body,
		CancellationToken cancellationToken)
		=> await _github.Issue
			.Update(owner, name, number, new IssueUpdate { Body = body })
			.ConfigureAwait(false);

	/// <inheritdoc />
	public async Task CommentAsync(
		string owner,
		string name,
		int number,
		string body,
		CancellationToken cancellationToken)
		=> await _github.Issue.Comment.Create(owner, name, number, body).ConfigureAwait(false);

	/// <inheritdoc />
	/// <remarks>
	/// Closed through the issue endpoint rather than the pull request one: a pull request is an issue,
	/// closing is the same state change, and this avoids needing a second permission surface. The
	/// branch is deliberately left in place — Dependabot owns it and tidies its own.
	/// </remarks>
	public async Task ClosePullRequestAsync(
		string owner,
		string name,
		int number,
		CancellationToken cancellationToken)
		=> await _github.Issue
			.Update(owner, name, number, new IssueUpdate { State = ItemState.Closed })
			.ConfigureAwait(false);
}
