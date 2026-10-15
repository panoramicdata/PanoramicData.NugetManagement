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
	/// Closed as "not planned" rather than "completed": the gap it described turned out not to be one,
	/// which is not the same as it having been fixed, and the distinction survives in the timeline.
	/// </remarks>
	public async Task CloseIssueAsync(
		string owner,
		string name,
		int number,
		CancellationToken cancellationToken)
		=> await _github.Issue
			.Update(owner, name, number, new IssueUpdate
			{
				State = ItemState.Closed,
				StateReason = ItemStateReason.NotPlanned
			})
			.ConfigureAwait(false);

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

	/// <inheritdoc />
	public async Task<int> CreatePullRequestAsync(
		string owner,
		string name,
		string head,
		string baseBranch,
		string title,
		string body,
		CancellationToken cancellationToken)
	{
		var created = await _github.PullRequest
			.Create(owner, name, new NewPullRequest(title, head, baseBranch) { Body = body })
			.ConfigureAwait(false);

		return created.Number;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<CheckRunState>> GetCheckRunsAsync(
		string owner,
		string name,
		string sha,
		CancellationToken cancellationToken)
	{
		var response = await _github.Check.Run.GetAllForReference(owner, name, sha).ConfigureAwait(false);

		return [.. response.CheckRuns.Select(run => new CheckRunState(run.Name, Classify(run)))];
	}

	/// <inheritdoc />
	/// <remarks>
	/// The expected SHA is sent so GitHub itself refuses a merge whose head has moved, rather than this
	/// code checking and then merging a different commit in the gap between.
	/// </remarks>
	public async Task MergePullRequestAsync(
		string owner,
		string name,
		int number,
		string expectedHeadSha,
		CancellationToken cancellationToken)
		=> await _github.PullRequest
			.Merge(owner, name, number, new MergePullRequest
			{
				Sha = expectedHeadSha,
				MergeMethod = PullRequestMergeMethod.Squash
			})
			.ConfigureAwait(false);

	private static CheckRunConclusion Classify(CheckRun run)
	{
		if (run.Status != CheckStatus.Completed)
		{
			return CheckRunConclusion.Pending;
		}

		return run.Conclusion?.Value is CheckConclusion.Success or CheckConclusion.Neutral or CheckConclusion.Skipped
			? CheckRunConclusion.Passed
			: CheckRunConclusion.Failed;
	}
}
