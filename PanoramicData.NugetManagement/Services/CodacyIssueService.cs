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

		try
		{
			var issues = await SearchIssuesAsync(client, organizationName, repositoryName, branch, cancellationToken)
				.ConfigureAwait(false);

			return new CodacyRepositoryReport { IsTracked = true, Issues = issues };
		}
		catch (Exception ex) when (CodacyNotFound.Matches(ex))
		{
			// Codacy does not have the repository under this name. It may still have it under the name
			// it was added with: see CodacyRepositoryNameResolver.
			var codacyName = await CodacyRepositoryNameResolver
				.ResolveAsync(
					CodacyRepositoryNameResolver.ForOrganization(client, organizationName),
					repositoryName,
					cancellationToken)
				.ConfigureAwait(false);

			if (codacyName is null)
			{
				// Codacy does not track this repository (not added or not yet analysed).
				return new CodacyRepositoryReport { IsTracked = false };
			}

			try
			{
				var issues = await SearchIssuesAsync(client, organizationName, codacyName, branch, cancellationToken)
					.ConfigureAwait(false);

				return new CodacyRepositoryReport { IsTracked = true, Issues = issues };
			}
			catch (Exception retry) when (CodacyNotFound.Matches(retry))
			{
				return new CodacyRepositoryReport { IsTracked = false };
			}
		}
	}

	/// <summary>
	/// Reads every page of Codacy's open issues for a repository branch.
	/// </summary>
	private static async Task<List<CodacyIssue>> SearchIssuesAsync(
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

		return issues;
	}
}
