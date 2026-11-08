using Codacy.Api;
using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Fetches Codacy's repository-level grade for a branch.
/// </summary>
public interface ICodacyRepositoryGradeService
{
	/// <summary>
	/// Retrieves the repository grade Codacy holds for a branch.
	/// </summary>
	/// <param name="apiToken">The Codacy API token.</param>
	/// <param name="organizationName">The GitHub organization name.</param>
	/// <param name="repositoryName">The repository name.</param>
	/// <param name="branch">The branch to query; null asks about Codacy's own default branch.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<CodacyRepositoryGrade> GetGradeAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICodacyRepositoryGradeService"/> backed by the Codacy.Api client.
/// </summary>
public sealed class CodacyRepositoryGradeService : ICodacyRepositoryGradeService
{
	/// <inheritdoc />
	public async Task<CodacyRepositoryGrade> GetGradeAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		using var client = new CodacyClient(new CodacyClientOptions { ApiToken = apiToken });

		var analysis = await GetAnalysisAsync(client, organizationName, repositoryName, branch, cancellationToken)
			.ConfigureAwait(false);

		return analysis is not null
			? await GradedAsync(client, organizationName, repositoryName, branch, analysis, cancellationToken).ConfigureAwait(false)
			: await NotFoundAsync(client, organizationName, repositoryName, branch, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<CodacyRepositoryGrade> GradedAsync(
		CodacyClient client,
		string organizationName,
		string repositoryName,
		string? branch,
		RepositoryWithAnalysis analysis,
		CancellationToken cancellationToken)
		=> new()
		{
			IsTracked = true,
			CodacyDefaultBranch = analysis.Repository?.DefaultBranch?.Name,
			Grade = analysis.Grade,
			GradeLetter = analysis.GradeLetter,
			IssuesPercentage = analysis.IssuesPercentage,
			ComplexFilesPercentage = analysis.ComplexFilesPercentage,
			DuplicationPercentage = analysis.DuplicationPercentage,
			AnalysisState = await CodacyAnalysisStateLookup.ResolveAsync(
				client,
				organizationName,
				repositoryName,
				branch,
				DateTimeOffset.UtcNow,
				cancellationToken).ConfigureAwait(false)
		};

	private static async Task<CodacyRepositoryGrade> NotFoundAsync(
		CodacyClient client,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		if (!await CodacyRepositoryLookup.IsAddedAsync(client, organizationName, repositoryName, cancellationToken).ConfigureAwait(false))
		{
			return new CodacyRepositoryGrade { IsTracked = false };
		}

		// The repository is held, so the 404 was the branch: Codacy grades some other branch.
		var codacyDefault = branch is null
			? null
			: await GetAnalysisAsync(client, organizationName, repositoryName, null, cancellationToken).ConfigureAwait(false);

		return new CodacyRepositoryGrade
		{
			IsTracked = true,
			IsBranchAnalysed = false,
			CodacyDefaultBranch = codacyDefault?.Repository?.DefaultBranch?.Name
		};
	}

	/// <summary>
	/// Reads the repository analysis, or returns <see langword="null"/> when Codacy answers 404.
	/// </summary>
	private static async Task<RepositoryWithAnalysis?> GetAnalysisAsync(
		CodacyClient client,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		try
		{
			return (await client.Analysis
				.GetRepositoryWithAnalysisAsync(Provider.Github, organizationName, repositoryName, branch, cancellationToken)
				.ConfigureAwait(false)).Data;
		}
		catch (Exception ex) when (CodacyNotFound.Matches(ex))
		{
			return null;
		}
	}
}
