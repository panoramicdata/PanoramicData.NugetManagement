using Codacy.Api;
using Codacy.Api.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// One page of an organization's repository names as Codacy spells them.
/// </summary>
internal sealed class CodacyRepositoryNamePage
{
	/// <summary>The names on this page. Entries Codacy returned without a name are null.</summary>
	public required IReadOnlyList<string?> Names { get; init; }

	/// <summary>The cursor for the next page, or null when this is the last.</summary>
	public string? Cursor { get; init; }
}

/// <summary>
/// Recovers the name Codacy holds for a repository whose provider name has since changed case.
/// </summary>
/// <remarks>
/// Codacy records a repository's name when it is added and does not follow later renames on the
/// provider, and its v3 paths match that name case-sensitively. Dell.CloudIQ.Api had been added as
/// Dell.CloudIq.Api, so every call under its current name answered 404 and CQ-03 reported "Codacy
/// does not know this repository — it has not been added" while the Codacy dashboard showed the
/// same repository analysed and graded A. A 404 therefore only means "not under this name"; only an
/// organization listing with no case-insensitive match means the repository was never added.
/// </remarks>
internal static class CodacyRepositoryNameResolver
{
	/// <summary>
	/// The largest page Codacy's repository listing accepts, matching the file listing's limit.
	/// </summary>
	private const int PageSize = 100;

	/// <summary>
	/// Finds the organization's repository whose name differs from <paramref name="repositoryName"/>
	/// only by case.
	/// </summary>
	/// <param name="listPageAsync">Fetches one page of names for a cursor.</param>
	/// <param name="repositoryName">The provider's current name for the repository.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Codacy's own spelling, or <see langword="null"/> when the listing holds the exact
	/// name — in which case the 404 was about something other than case — or no match at all.</returns>
	public static async Task<string?> ResolveAsync(
		Func<string?, CancellationToken, Task<CodacyRepositoryNamePage>> listPageAsync,
		string repositoryName,
		CancellationToken cancellationToken)
	{
		string? cursor = null;

		do
		{
			var page = await listPageAsync(cursor, cancellationToken).ConfigureAwait(false);

			foreach (var name in page.Names)
			{
				if (name is null || !string.Equals(name, repositoryName, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				return string.Equals(name, repositoryName, StringComparison.Ordinal) ? null : name;
			}

			cursor = page.Cursor;
		}
		while (!string.IsNullOrEmpty(cursor));

		return null;
	}

	/// <summary>
	/// Builds the page fetcher that reads an organization's repositories from Codacy.
	/// </summary>
	public static Func<string?, CancellationToken, Task<CodacyRepositoryNamePage>> ForOrganization(
		CodacyClient client,
		string organizationName)
		=> async (cursor, cancellationToken) =>
		{
			var page = await client.Organizations.ListOrganizationRepositoriesAsync(
				Provider.Github,
				organizationName,
				cursor,
				PageSize,
				null,
				null,
				null,
				null,
				cancellationToken).ConfigureAwait(false);

			return new CodacyRepositoryNamePage
			{
				Names = [.. page.Data.Select(repository => repository.Name)],
				Cursor = page.Pagination?.Cursor
			};
		};
}
