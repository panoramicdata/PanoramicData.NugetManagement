using Codacy.Api;
using Codacy.Api.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// What Codacy says about a repository's grade badge.
/// </summary>
/// <param name="IsAdded">Whether Codacy holds the repository at all.</param>
/// <param name="GradeBadgeUrl">The grade badge image URL Codacy reports, when it holds the repository.</param>
public sealed record CodacyBadgeInfo(bool IsAdded, string? GradeBadgeUrl);

/// <summary>
/// Reads a repository's real grade badge from Codacy.
/// </summary>
public interface ICodacyBadgeService
{
	/// <summary>
	/// Retrieves the grade badge Codacy reports for a repository.
	/// </summary>
	/// <param name="apiToken">The Codacy API token.</param>
	/// <param name="organizationName">The GitHub organization name.</param>
	/// <param name="repositoryName">The repository name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<CodacyBadgeInfo> GetBadgeAsync(string apiToken, string organizationName, string repositoryName, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICodacyBadgeService"/> backed by the Codacy.Api client.
/// </summary>
public sealed class CodacyBadgeService : ICodacyBadgeService
{
	/// <inheritdoc />
	public async Task<CodacyBadgeInfo> GetBadgeAsync(string apiToken, string organizationName, string repositoryName, CancellationToken cancellationToken)
	{
		using var client = new CodacyClient(new CodacyClientOptions { ApiToken = apiToken });
		try
		{
			var response = await client.Repositories
				.GetRepositoryAsync(Provider.Github, organizationName, repositoryName, cancellationToken)
				.ConfigureAwait(false);

			return new CodacyBadgeInfo(true, response.Data?.Badges?.Grade);
		}
		catch (Exception ex) when (CodacyNotFound.Matches(ex))
		{
			return new CodacyBadgeInfo(false, null);
		}
	}
}
