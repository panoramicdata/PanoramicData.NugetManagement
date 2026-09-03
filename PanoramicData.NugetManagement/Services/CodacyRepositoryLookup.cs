using Codacy.Api;
using Codacy.Api.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Asks Codacy the one question that settles whether a repository has been added.
/// </summary>
/// <remarks>
/// The repository endpoint answers 200 for a repository Codacy holds whatever state its analysis is
/// in, and 404 only when it holds nothing under that name. It is case-sensitive, so a repository
/// whose identity disagrees with GitHub on case still reads as not added — the defect the reverted
/// case-insensitive fallback was hiding stays visible.
/// </remarks>
internal static class CodacyRepositoryLookup
{
	/// <summary>
	/// Whether Codacy holds the repository under exactly this name.
	/// </summary>
	/// <remarks>
	/// Anything other than a 404 propagates. An unreachable Codacy leaves the question unanswered,
	/// and reporting that as "not added" is the mistake this whole path exists to stop.
	/// </remarks>
	public static async Task<bool> IsAddedAsync(
		CodacyClient client,
		string organizationName,
		string repositoryName,
		CancellationToken cancellationToken)
	{
		try
		{
			_ = await client.Repositories
				.GetRepositoryAsync(Provider.Github, organizationName, repositoryName, cancellationToken)
				.ConfigureAwait(false);

			return true;
		}
		catch (Exception ex) when (CodacyNotFound.Matches(ex))
		{
			return false;
		}
	}
}
