using System.Text.Json;
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
		var runs = await _github.Check.Run.GetAllForReference(owner, name, sha).ConfigureAwait(false);

		// The older commit-status API too: an integration that reports only that way would otherwise look
		// like a repository with no CI. A combined status with no statuses is empty, not pending.
		var statuses = await _github.Repository.Status.GetCombined(owner, name, sha).ConfigureAwait(false);

		return
		[
			.. runs.CheckRuns.Select(run => new CheckRunState(
				run.Name,
				GitHubCheckMapping.FromCheckRun(run.Status.Value, run.Conclusion?.Value))),
			.. statuses.Statuses.Select(status => new CheckRunState(
				status.Context,
				GitHubCheckMapping.FromStatus(status.State.Value)))
		];
	}

	/// <inheritdoc />
	/// <remarks>Only GraphQL can turn auto-merge on; the REST pull request resource supplies its node id.</remarks>
	public async Task<AutoMergeResult> EnableAutoMergeAsync(
		string owner,
		string name,
		int number,
		string expectedHeadSha,
		CancellationToken cancellationToken)
	{
		try
		{
			var pullRequest = await _github.PullRequest.Get(owner, name, number).ConfigureAwait(false);
			var body = JsonSerializer.Serialize(new
			{
				query = "mutation($id:ID!,$sha:GitObjectID!,$headline:String!,$body:String!){enablePullRequestAutoMerge(input:{pullRequestId:$id,mergeMethod:SQUASH,expectedHeadOid:$sha,commitHeadline:$headline,commitBody:$body}){pullRequest{autoMergeRequest{enabledAt}}}}",
				// Explicit, so the governance trailer survives whatever the repository's squash message setting is.
				variables = new { id = pullRequest.NodeId, sha = expectedHeadSha, headline = pullRequest.Title, body = SquashBody(pullRequest.Body, pullRequest.Title) }
			});

			var response = await _github.Connection
				.Post<string>(new Uri("graphql", UriKind.Relative), body, "application/json", "application/json")
				.ConfigureAwait(false);

			// Octokit leaves the typed Body empty for this response; only the raw body holds the JSON.
			return ParseAutoMergeResponse(response.HttpResponse.Body as string);
		}
		catch (Exception ex) when (ex is ApiException or HttpRequestException)
		{
			return new AutoMergeResult(AutoMergeState.Refused, ex.Message);
		}
	}

	/// <summary>The squash commit body: the pull request body without a first line repeating the title.</summary>
	/// <param name="body">The pull request body, which holds the full commit message.</param>
	/// <param name="title">The pull request title, used as the commit headline.</param>
	public static string SquashBody(string? body, string title)
	{
		if (string.IsNullOrEmpty(body))
		{
			return string.Empty;
		}

		var normalised = body.Replace("\r\n", "\n", StringComparison.Ordinal);
		var firstBreak = normalised.IndexOf('\n', StringComparison.Ordinal);
		var firstLine = firstBreak < 0 ? normalised : normalised[..firstBreak];

		return firstLine.Trim() == title.Trim()
			? (firstBreak < 0 ? string.Empty : normalised[firstBreak..].TrimStart('\n'))
			: normalised;
	}

	/// <summary>
	/// Reads GitHub's answer to <c>enablePullRequestAutoMerge</c>, which reports a refusal as HTTP 200
	/// with an <c>errors</c> array.
	/// </summary>
	/// <param name="json">The raw response body.</param>
	/// <returns>
	/// Enabled when an auto-merge request came back; already mergeable when GitHub refused because the
	/// pull request is in clean or unstable status, which means no required check is pending; refused
	/// otherwise, including when the answer cannot be read.
	/// </returns>
	public static AutoMergeResult ParseAutoMergeResponse(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new AutoMergeResult(AutoMergeState.Refused, "GitHub returned nothing.");
		}

		try
		{
			using var document = JsonDocument.Parse(json);
			return ReadAutoMergeAnswer(document.RootElement);
		}
		catch (JsonException)
		{
			return new AutoMergeResult(AutoMergeState.Refused, "GitHub's answer was not JSON.");
		}
	}

	private static AutoMergeResult ReadAutoMergeAnswer(JsonElement root)
	{
		var error = FirstErrorMessage(root);
		if (error is not null)
		{
			var mergeable = error.Contains("clean status", StringComparison.OrdinalIgnoreCase)
				|| error.Contains("unstable status", StringComparison.OrdinalIgnoreCase);

			return new AutoMergeResult(mergeable ? AutoMergeState.AlreadyMergeable : AutoMergeState.Refused, error);
		}

		var enabled = root.TryGetProperty("data", out var data)
			&& data.ValueKind == JsonValueKind.Object
			&& data.TryGetProperty("enablePullRequestAutoMerge", out var mutation)
			&& mutation.ValueKind == JsonValueKind.Object;

		return enabled
			? new AutoMergeResult(AutoMergeState.Enabled, null)
			: new AutoMergeResult(AutoMergeState.Refused, "GitHub did not confirm auto-merge.");
	}

	private static string? FirstErrorMessage(JsonElement root)
		=> root.ValueKind == JsonValueKind.Object
			&& root.TryGetProperty("errors", out var errors)
			&& errors.ValueKind == JsonValueKind.Array
			&& errors.GetArrayLength() > 0
				? errors[0].TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty
				: null;

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
}
