using Codacy.Api.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Fetches one page of security items, returning the items and the cursor for the next page.
/// </summary>
/// <param name="cursor">The cursor to resume from, or null for the first page.</param>
/// <param name="cancellationToken">Cancellation token.</param>
internal delegate Task<(IReadOnlyList<SrmItem> Items, string? NextCursor)> CodacySecurityPageFetcher(
	string? cursor,
	CancellationToken cancellationToken);

/// <summary>
/// Drains a cursor-paged Codacy security search.
/// </summary>
/// <remarks>
/// Codacy caps a page at 100 items and a single repository can hold more than that — MagicSuite
/// carries 147 open Critical findings alone — so reading only the first page would under-report
/// exactly the repositories that most need reporting.
/// </remarks>
internal static class CodacySecurityPager
{
	/// <summary>
	/// Reads every page, following the cursor until Codacy stops issuing one.
	/// </summary>
	/// <remarks>
	/// Stops if a page hands back the same cursor it was given. Codacy has no reason to do that, but
	/// an unguarded cursor loop against a live API is an infinite one, and this runs unattended
	/// across eighty repositories.
	/// </remarks>
	public static async Task<IReadOnlyList<SrmItem>> DrainAsync(
		CodacySecurityPageFetcher fetchPage,
		CancellationToken cancellationToken)
	{
		var all = new List<SrmItem>();
		string? cursor = null;

		while (true)
		{
			var (items, nextCursor) = await fetchPage(cursor, cancellationToken).ConfigureAwait(false);
			all.AddRange(items);

			if (string.IsNullOrWhiteSpace(nextCursor) || string.Equals(nextCursor, cursor, StringComparison.Ordinal))
			{
				return all;
			}

			cursor = nextCursor;
		}
	}
}
