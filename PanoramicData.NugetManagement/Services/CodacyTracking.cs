using System.Collections.Concurrent;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// What Codacy turned out to hold for a repository whose listing answered 404.
/// </summary>
internal enum CodacyTrackingState
{
	/// <summary>Codacy holds nothing under this name: the repository was never added.</summary>
	NotAdded,

	/// <summary>Codacy holds the repository, but has listed nothing for the branch asked about.</summary>
	AddedButNotListed,

	/// <summary>The listing answered on a second ask, so the 404 was a miss rather than a fact.</summary>
	Listed
}

/// <summary>
/// Rations the one retry a repository gets after a Codacy listing 404.
/// </summary>
/// <remarks>
/// Three rules ask Codacy about the same repository in a single assessment, and a sweep covers
/// eighty of them, so an unrationed retry multiplies the traffic that produced the 404 in the first
/// place. One an hour per repository is enough: the repositories that need a retry are the ones
/// Codacy has not finished analysing, and that does not resolve in seconds.
/// </remarks>
internal sealed class CodacyRetryWindow(TimeSpan window)
{
	/// <summary>The window every production caller shares, so the ration is per repository rather
	/// than per service instance — each rule constructs its own service.</summary>
	public static CodacyRetryWindow Shared { get; } = new(TimeSpan.FromHours(1));

	private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRetry = new(StringComparer.Ordinal);

	/// <summary>
	/// Claims <paramref name="key"/>'s retry for the current window, or refuses when it is already spent.
	/// </summary>
	/// <remarks>
	/// Every rule in a parallel sweep reaches this line at the same moment, so the claim has to be a
	/// compare-and-swap. <see cref="ConcurrentDictionary{TKey, TValue}.AddOrUpdate(TKey, Func{TKey, TValue}, Func{TKey, TValue, TValue})"/>
	/// is not one: it may run its factory more than once under contention, and setting a flag inside
	/// the factory handed the retry to two of sixty-four callers. Only <c>TryAdd</c> and the
	/// three-argument <c>TryUpdate</c> settle the race, so a caller that loses one re-reads and
	/// decides again against what the winner wrote.
	/// </remarks>
	public bool TryBeginRetry(string key, DateTimeOffset now)
	{
		while (true)
		{
			if (_lastRetry.TryGetValue(key, out var last))
			{
				if (now - last < window)
				{
					return false;
				}

				if (_lastRetry.TryUpdate(key, now, last))
				{
					return true;
				}

				continue;
			}

			if (_lastRetry.TryAdd(key, now))
			{
				return true;
			}
		}
	}
}

/// <summary>
/// Decides what a 404 from a repository-scoped Codacy listing is entitled to conclude.
/// </summary>
/// <remarks>
/// Codacy answered the file listing for panoramicdata/ConnectWise.Manage.Api with a 404 while
/// holding the repository — added 2026-09-01, default branch <c>main</c>, enabled — and the file
/// grade service read that 404 as proof of absence, so CQ-03 reported "Codacy does not know this
/// repository — it has not been added" about a repository whose Codacy dashboard was open in the
/// next window. Eleven repositories failed that way in one sweep; six answered 200 an hour later,
/// and those six were exactly the batch added on 2026-09-01.
/// <para>
/// Only the repository endpoint answering 404 for the same name establishes absence. It is
/// case-sensitive — <c>Dell.CloudIQ.Api</c> 404s where <c>Dell.CloudIq.Api</c> does not — so
/// corroborating through it leaves a repository whose declared URL disagrees with GitHub on case
/// reading as not added, which is the defect the case-insensitive fallback was reverted for hiding.
/// </para>
/// </remarks>
internal static class CodacyTracking
{
	/// <summary>
	/// Establishes whether a listing 404 means the repository is absent, unanalysed, or merely missed.
	/// </summary>
	/// <param name="isAddedAsync">Asks Codacy whether it holds the repository under this exact name.</param>
	/// <param name="retryListingAsync">Asks for the listing a second time, returning whether it answered.</param>
	/// <param name="window">The retry ration.</param>
	/// <param name="key">Identifies the repository within <paramref name="window"/>.</param>
	/// <param name="now">The current time, for the ration.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public static async Task<CodacyTrackingState> ResolveAsync(
		Func<CancellationToken, Task<bool>> isAddedAsync,
		Func<CancellationToken, Task<bool>> retryListingAsync,
		CodacyRetryWindow window,
		string key,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		if (!await isAddedAsync(cancellationToken).ConfigureAwait(false))
		{
			// Codacy holds nothing under this name. A retry asks the same question of the same absence.
			return CodacyTrackingState.NotAdded;
		}

		if (!window.TryBeginRetry(key, now))
		{
			return CodacyTrackingState.AddedButNotListed;
		}

		return await retryListingAsync(cancellationToken).ConfigureAwait(false)
			? CodacyTrackingState.Listed
			: CodacyTrackingState.AddedButNotListed;
	}
}
