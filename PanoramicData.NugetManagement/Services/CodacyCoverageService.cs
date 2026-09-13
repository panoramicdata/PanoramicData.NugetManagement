using Codacy.Api;
using Codacy.Api.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Reads the line coverage percentage Codacy holds for a repository.
/// </summary>
public interface ICodacyCoverageService
{
	/// <summary>
	/// The line coverage Codacy holds for a repository branch.
	/// </summary>
	/// <param name="apiToken">The Codacy API token.</param>
	/// <param name="organizationName">The GitHub organization name.</param>
	/// <param name="repositoryName">The repository name.</param>
	/// <param name="branch">The branch to query (typically the default branch); may be null.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>
	/// The percentage, or null when Codacy holds no coverage for the repository — either because it
	/// was never added, or because nothing has ever been uploaded.
	/// </returns>
	Task<double?> GetLineCoveragePercentAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICodacyCoverageService"/> backed by the Codacy.Api client.
/// </summary>
public sealed class CodacyCoverageService : ICodacyCoverageService
{
	/// <inheritdoc />
	public async Task<double?> GetLineCoveragePercentAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		using var client = new CodacyClient(new CodacyClientOptions { ApiToken = apiToken });

		return await ReadCoverageAsync(
			async token => (await client.Analysis
				.GetRepositoryWithAnalysisAsync(Provider.Github, organizationName, repositoryName, branch, token)
				.ConfigureAwait(false)).Data.Coverage?.CoveragePercentageWithDecimals,
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The same read over a supplied reader, so the failure handling can be tested without a Codacy
	/// account.
	/// </summary>
	/// <remarks>
	/// Only a 404 becomes null. Anything else propagates: an unreachable Codacy reported as "no
	/// coverage" would grade every repository RED on a network blip, and RED is what the AI fixer
	/// acts on. This is the same distinction <see cref="CodacyRepositoryLookup"/> exists to preserve.
	/// </remarks>
	internal static async Task<double?> ReadCoverageAsync(
		Func<CancellationToken, Task<double?>> readAsync,
		CancellationToken cancellationToken)
	{
		try
		{
			return await readAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (CodacyNotFound.Matches(ex))
		{
			return null;
		}
	}
}
