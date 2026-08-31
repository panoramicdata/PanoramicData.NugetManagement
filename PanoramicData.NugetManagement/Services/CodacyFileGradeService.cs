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

		try
		{
			var files = await ListFilesAsync(client, organizationName, repositoryName, branch, cancellationToken)
				.ConfigureAwait(false);

			return new CodacyFileGradeReport { IsTracked = true, Files = files };
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
				// Not under any spelling of the name: it was never added.
				return new CodacyFileGradeReport { IsTracked = false };
			}

			try
			{
				var files = await ListFilesAsync(client, organizationName, codacyName, branch, cancellationToken)
					.ConfigureAwait(false);

				return new CodacyFileGradeReport
				{
					IsTracked = true,
					Files = files,
					CodacyRepositoryName = codacyName
				};
			}
			catch (Exception retry) when (CodacyNotFound.Matches(retry))
			{
				// Listed under this name but still a 404 for its files: nothing more to try here.
				return new CodacyFileGradeReport { IsTracked = false };
			}
		}
	}

	/// <summary>
	/// Reads every page of Codacy's file listing for a branch.
	/// </summary>
	private static async Task<List<CodacyFileGrade>> ListFilesAsync(
		CodacyClient client,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		var files = new List<CodacyFileGrade>();
		string? cursor = null;

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

		return files;
	}
}
