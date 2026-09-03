using Codacy.Api;
using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Fetches Codacy's per-file grades for a repository branch.
/// </summary>
public interface ICodacyFileGradeService
{
	/// <summary>
	/// Retrieves the file grades Codacy holds for a repository branch.
	/// </summary>
	/// <param name="apiToken">The Codacy API token.</param>
	/// <param name="organizationName">The GitHub organization name.</param>
	/// <param name="repositoryName">The repository name.</param>
	/// <param name="branch">The branch to query (typically the default branch); may be null.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The repository's file grade report. <see cref="CodacyFileGradeReport.IsTracked"/> is
	/// <see langword="false"/> when Codacy does not know about the repository.</returns>
	Task<CodacyFileGradeReport> GetGradesAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICodacyFileGradeService"/> backed by the Codacy.Api client.
/// </summary>
public sealed class CodacyFileGradeService : ICodacyFileGradeService
{
	/// <summary>
	/// The largest page Codacy's file listing accepts. Anything above this is a 400: asking for 500
	/// in one go returned one, and the whole gate fell into its caller's catch and reported itself
	/// unevaluated.
	/// </summary>
	private const int PageSize = 100;

	/// <inheritdoc />
	public async Task<CodacyFileGradeReport> GetGradesAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		using var client = new CodacyClient(new CodacyClientOptions { ApiToken = apiToken });

		var files = await ListFilesAsync(client, organizationName, repositoryName, branch, cancellationToken)
			.ConfigureAwait(false);

		// What the grades describe. Fetched here rather than in the rules because it is the same
		// question about the same response, and asking it twice would double the calls for CQ-03 and
		// CQ-06 and let the two disagree about one repository in one panel.
		Task<CodacyAnalysisState?> AnalysisStateAsync() => CodacyAnalysisStateLookup.ResolveAsync(
			client,
			organizationName,
			repositoryName,
			branch,
			DateTimeOffset.UtcNow,
			cancellationToken);

		if (files is not null)
		{
			return new CodacyFileGradeReport
			{
				IsTracked = true,
				Files = files,
				AnalysisState = await AnalysisStateAsync().ConfigureAwait(false)
			};
		}

		// A listing 404 does not establish that the repository was never added — Codacy answered it
		// for repositories it demonstrably held. Ask the question that does.
		var state = await CodacyTracking.ResolveAsync(
			isAddedAsync: token => CodacyRepositoryLookup.IsAddedAsync(client, organizationName, repositoryName, token),
			retryListingAsync: async token =>
			{
				files = await ListFilesAsync(client, organizationName, repositoryName, branch, token)
					.ConfigureAwait(false);
				return files is not null;
			},
			window: CodacyRetryWindow.Shared,
			key: $"{organizationName}/{repositoryName}",
			now: DateTimeOffset.UtcNow,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		if (state is CodacyTrackingState.NotAdded)
		{
			// Never added, so there is no analysis to describe and no call worth making.
			return new CodacyFileGradeReport { IsTracked = false };
		}

		return new CodacyFileGradeReport
		{
			IsTracked = true,

			// Added, but nothing listed for the branch, means no files. CQ-03 reports that as its own
			// failure, which names the analysis that has not run rather than an integration that was
			// never set up — and the analysis state is exactly what tells it whether one is running now.
			Files = state is CodacyTrackingState.Listed ? files ?? [] : [],
			AnalysisState = await AnalysisStateAsync().ConfigureAwait(false)
		};
	}

	/// <summary>
	/// Pages the whole file listing, or returns <see langword="null"/> when Codacy answers 404.
	/// </summary>
	private static async Task<List<CodacyFileGrade>?> ListFilesAsync(
		CodacyClient client,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		var files = new List<CodacyFileGrade>();
		string? cursor = null;

		try
		{
			do
			{
				var page = await client.Repositories.ListFilesAsync(
					Provider.Github,
					organizationName,
					repositoryName,
					branch,
					null,
					null,
					null,
					cursor,
					PageSize,
					cancellationToken).ConfigureAwait(false);

				files.AddRange(page.Data.Select(file => new CodacyFileGrade
				{
					Path = file.Path,
					GradeLetter = file.GradeLetter,
					Grade = file.Grade,
					TotalIssues = file.TotalIssues,
					Complexity = file.Complexity,
					Duplication = file.Duplication,
					NumberOfClones = file.NumberOfClones,
					LinesOfCode = file.LinesOfCode
				}));

				cursor = page.Pagination?.Cursor;
			}
			while (!string.IsNullOrEmpty(cursor));
		}
		catch (Exception ex) when (CodacyNotFound.Matches(ex))
		{
			return null;
		}

		return files;
	}
}
