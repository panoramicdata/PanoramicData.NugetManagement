using Codacy.Api;
using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Fetches the list of open Codacy issues for a repository via the Codacy API.
/// </summary>
public interface ICodacyIssueService
{
	/// <summary>
	/// Retrieves the open Codacy issues for a repository on the given branch.
	/// </summary>
	/// <param name="apiToken">The Codacy API token.</param>
	/// <param name="organizationName">The GitHub organization name.</param>
	/// <param name="repositoryName">The repository name.</param>
	/// <param name="branch">The branch to query (typically the default branch); may be null.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The repository's issue report. <see cref="CodacyRepositoryReport.IsTracked"/> is
	/// <see langword="false"/> when Codacy does not know about the repository.</returns>
	Task<CodacyRepositoryReport> GetReportAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICodacyIssueService"/> backed by the Codacy.Api client.
/// </summary>
public sealed class CodacyIssueService : ICodacyIssueService
{
	private const int PageSize = 100;

	/// <inheritdoc />
	public async Task<CodacyRepositoryReport> GetReportAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		using var client = new CodacyClient(new CodacyClientOptions { ApiToken = apiToken });

		var issues = await SearchIssuesAsync(client, organizationName, repositoryName, branch, cancellationToken)
			.ConfigureAwait(false);

		if (issues is not null)
		{
			return new CodacyRepositoryReport { IsTracked = true, Issues = issues };
		}

		// The same 404 the file listing answers for repositories Codacy holds, and it must not be read
		// as absence here either: CQ-05 reports an untracked repository as "no issues to report",
		// which turns a repository Codacy has graded into a silent pass.
		var state = await CodacyTracking.ResolveAsync(
			isAddedAsync: token => CodacyRepositoryLookup.IsAddedAsync(client, organizationName, repositoryName, token),
			retryListingAsync: async token =>
			{
				issues = await SearchIssuesAsync(client, organizationName, repositoryName, branch, token)
					.ConfigureAwait(false);
				return issues is not null;
			},
			window: CodacyRetryWindow.Shared,
			key: $"{organizationName}/{repositoryName}/issues",
			now: DateTimeOffset.UtcNow,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		return state switch
		{
			CodacyTrackingState.NotAdded => new CodacyRepositoryReport { IsTracked = false },
			CodacyTrackingState.Listed => new CodacyRepositoryReport { IsTracked = true, Issues = issues ?? [] },
			_ => new CodacyRepositoryReport { IsTracked = true, Issues = [] }
		};
	}

	/// <summary>
	/// Pages the whole issue search, or returns <see langword="null"/> when Codacy answers 404.
	/// </summary>
	private static async Task<List<CodacyIssue>?> SearchIssuesAsync(
		CodacyClient client,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		var body = new SearchRepositoryIssuesBody();
		if (!string.IsNullOrWhiteSpace(branch))
		{
			body.BranchName = branch;
		}

		var issues = new List<CodacyIssue>();
		string? cursor = null;

		try
		{
			do
			{
				var response = await client.Analysis.SearchRepositoryIssuesAsync(
					Provider.Github,
					organizationName,
					repositoryName,
					body,
					cursor,
					PageSize,
					cancellationToken).ConfigureAwait(false);

				if (response.Data is not null)
				{
					foreach (var issue in response.Data)
					{
						issues.Add(new CodacyIssue
						{
							FilePath = issue.FilePath ?? string.Empty,
							Line = issue.LineNumber,
							Message = issue.Message ?? string.Empty,
							PatternId = issue.PatternInfo?.Id,
							Category = issue.PatternInfo?.Category,
							Severity = issue.PatternInfo?.SeverityLevel.ToString(),
							Language = issue.Language
						});
					}
				}

				cursor = response.Pagination?.Cursor;
			}
			while (!string.IsNullOrEmpty(cursor));
		}
		catch (Exception ex) when (CodacyNotFound.Matches(ex))
		{
			return null;
		}

		return issues;
	}
}
