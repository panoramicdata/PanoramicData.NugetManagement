using Microsoft.Extensions.Logging;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Refreshes the committed record of what nuget.org has published.
/// </summary>
/// <remarks>
/// The only component that contacts nuget.org for dependency versions. The rules read the cache and
/// never make a request, which is what makes an assessment reproducible and offline-tolerant, and
/// what removed one round trip per package reference per rule from every run.
/// </remarks>
public sealed class NuGetVersionRefresher
{
	/// <summary>The most requests in flight at once. nuget.org is a shared service, not ours.</summary>
	private const int _maximumConcurrency = 4;

	private readonly NuGetVersionCache _cache;
	private readonly Func<string, CancellationToken, Task<(string Version, DateTimeOffset Published)?>> _lookup;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger<NuGetVersionRefresher> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetVersionRefresher"/> class.
	/// </summary>
	/// <param name="cache">The cache to fill.</param>
	/// <param name="lookup">Reads a package's latest stable version and publication date.</param>
	/// <param name="timeProvider">The clock used to stamp genuine changes.</param>
	/// <param name="logger">The logger.</param>
	public NuGetVersionRefresher(
		NuGetVersionCache cache,
		Func<string, CancellationToken, Task<(string Version, DateTimeOffset Published)?>> lookup,
		TimeProvider timeProvider,
		ILogger<NuGetVersionRefresher> logger)
	{
		_cache = cache;
		_lookup = lookup;
		_timeProvider = timeProvider;
		_logger = logger;
	}

	/// <summary>
	/// Refreshes every distinct package id given, and persists only if something changed.
	/// </summary>
	/// <param name="packageIds">The package ids to refresh; duplicates are asked once.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>How many packages had a different version from the one already held.</returns>
	public async Task<int> RefreshAsync(IEnumerable<string> packageIds, CancellationToken cancellationToken)
	{
		var distinct = packageIds
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		var now = _timeProvider.GetUtcNow();
		var changed = 0;

		using var limiter = new SemaphoreSlim(_maximumConcurrency);

		var sweeps = distinct.Select(async packageId =>
		{
			await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var latest = await _lookup(packageId, cancellationToken).ConfigureAwait(false);
				if (latest is null)
				{
					// A version known a minute ago beats no version at all: blanking it would turn a
					// transient nuget.org failure into "this package has never been published".
					return;
				}

				if (_cache.Update(packageId, latest.Value.Version, latest.Value.Published, now))
				{
					Interlocked.Increment(ref changed);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One unreadable package must not abandon the sweep: the rest of the estate still
				// deserves an up-to-date answer.
				_logger.LogWarning(ex, "Could not refresh {PackageId}", packageId);
			}
			finally
			{
				limiter.Release();
			}
		});

		await Task.WhenAll(sweeps).ConfigureAwait(false);

		if (changed > 0)
		{
			_cache.Persist();
			_logger.LogInformation("Refreshed {Changed} of {Total} NuGet package versions.", changed, distinct.Count);
		}

		return changed;
	}
}
