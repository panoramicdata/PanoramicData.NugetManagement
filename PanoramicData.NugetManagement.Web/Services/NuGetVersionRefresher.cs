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

	/// <summary>
	/// The most requests started per second. Concurrency alone does not bound request rate: four
	/// slots that each complete in twenty milliseconds is two hundred requests a second at a service
	/// nobody pays us to hammer, so the sweep is paced as well as capped.
	/// </summary>
	public const int DefaultRequestsPerSecond = 4;

	private readonly TimeSpan _minimumRequestSpacing;

	/// <summary>Serialises slot allocation so concurrent sweep tasks cannot claim the same instant.</summary>
	private readonly Lock _paceLock = new();

	private DateTimeOffset _nextRequestUtc = DateTimeOffset.MinValue;

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
	/// <param name="requestsPerSecond">
	/// The most requests to start per second; defaults to <see cref="DefaultRequestsPerSecond"/>.
	/// </param>
	public NuGetVersionRefresher(
		NuGetVersionCache cache,
		Func<string, CancellationToken, Task<(string Version, DateTimeOffset Published)?>> lookup,
		TimeProvider timeProvider,
		ILogger<NuGetVersionRefresher> logger,
		int requestsPerSecond = DefaultRequestsPerSecond)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerSecond, 1);

		_cache = cache;
		_lookup = lookup;
		_timeProvider = timeProvider;
		_logger = logger;
		_minimumRequestSpacing = TimeSpan.FromSeconds(1.0 / requestsPerSecond);
	}

	/// <summary>
	/// Waits until this request's paced slot arrives, reserving the next one as it goes.
	/// </summary>
	/// <remarks>
	/// Slot allocation is a read-then-write pair over shared state, so it is serialised; the wait
	/// itself happens outside the lock, and uses the injected <see cref="TimeProvider"/> so a test
	/// can prove the pacing without spending the seconds.
	/// </remarks>
	private async Task PaceAsync(CancellationToken cancellationToken)
	{
		TimeSpan wait;
		lock (_paceLock)
		{
			var now = _timeProvider.GetUtcNow();
			var slot = _nextRequestUtc > now ? _nextRequestUtc : now;
			_nextRequestUtc = slot + _minimumRequestSpacing;
			wait = slot - now;
		}

		if (wait > TimeSpan.Zero)
		{
			await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
		}
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
				await PaceAsync(cancellationToken).ConfigureAwait(false);

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
