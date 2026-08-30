using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Sweeps the estate's package ids against nuget.org on an interval, keeping the committed version
/// cache current.
/// </summary>
/// <remarks>
/// <para>
/// Without this the cache only ever contains what was seeded, so every package the seed did not
/// mention is a permanent miss and the freshness half of the gate never fires for it. Registering
/// the refresher without anything to drive it is the same as not having one.
/// </para>
/// <para>
/// The package-id universe is taken from the two stores rather than from a new discovery path.
/// <see cref="NuGetFloorCatalog"/> observes every package reference of every repository the
/// application assesses, and <see cref="NuGetVersionCache"/> holds everything previously refreshed,
/// so their union is exactly "packages this application has seen". That makes the sweep
/// self-sustaining: assessments feed the floor, the next sweep picks up what they added, and the
/// seed only has to bootstrap the first cycle.
/// </para>
/// </remarks>
public sealed class NuGetVersionRefreshService : BackgroundService
{
	/// <summary>
	/// How long between sweeps. Publication of a new package version is an event measured in days,
	/// and the shortest grace period is thirty of them, so four sweeps a day is ample; anything
	/// tighter spends requests at nuget.org to learn nothing.
	/// </summary>
	public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

	private readonly NuGetVersionCache _cache;
	private readonly NuGetFloorCatalog _floors;
	private readonly NuGetVersionRefresher _refresher;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger<NuGetVersionRefreshService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetVersionRefreshService"/> class.
	/// </summary>
	/// <param name="cache">The committed version cache, and one source of package ids.</param>
	/// <param name="floors">The learned floor catalogue, and the other source of package ids.</param>
	/// <param name="refresher">The only component that contacts nuget.org.</param>
	/// <param name="timeProvider">The clock the sweep interval is measured on.</param>
	/// <param name="logger">The logger.</param>
	public NuGetVersionRefreshService(
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		NuGetVersionRefresher refresher,
		TimeProvider timeProvider,
		ILogger<NuGetVersionRefreshService> logger)
	{
		_cache = cache;
		_floors = floors;
		_refresher = refresher;
		_timeProvider = timeProvider;
		_logger = logger;
	}

	/// <summary>
	/// The distinct package ids the estate is known to use: everything the cache holds, plus
	/// everything assessments have observed into the floor catalogue.
	/// </summary>
	public IReadOnlyCollection<string> PackageIds
	{
		get
		{
			var ids = new HashSet<string>(_cache.PackageIds, StringComparer.OrdinalIgnoreCase);
			ids.UnionWith(_floors.PackageIds);
			return ids;
		}
	}

	/// <summary>
	/// Runs one sweep of the union of both stores' package ids.
	/// </summary>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>How many packages had a different version from the one already held.</returns>
	public Task<int> RunSweepAsync(CancellationToken cancellationToken)
	{
		var packageIds = PackageIds;
		return packageIds.Count == 0
			? Task.FromResult(0)
			: _refresher.RefreshAsync(packageIds, cancellationToken);
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		ReportStoreLoadFailures();

		using var timer = new PeriodicTimer(SweepInterval, _timeProvider);

		try
		{
			do
			{
				await SweepSafelyAsync(stoppingToken).ConfigureAwait(false);
			}
			while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
		}
		catch (OperationCanceledException)
		{
			// Shutdown, not a fault.
		}
	}

	/// <summary>
	/// Says so, loudly, when either committed store was present but unreadable. Both fail silently
	/// by design — the gate stands down rather than judging repositories against nothing — which
	/// looks identical to a compliant estate unless somebody says otherwise.
	/// </summary>
	private void ReportStoreLoadFailures()
	{
		if (_cache.LoadFailed)
		{
			_logger.LogError(
				"The NuGet version cache could not be read ({Failure}); every package will report an "
				+ "unknown upstream version and no freshness finding can be raised.",
				_cache.LoadFailure);
		}

		if (_floors.LoadFailed)
		{
			_logger.LogError(
				"The NuGet floor catalogue could not be read ({Failure}); no package has a floor and "
				+ "the consistency half of the version gate is standing down.",
				_floors.LoadFailure);
		}
	}

	private async Task SweepSafelyAsync(CancellationToken cancellationToken)
	{
		try
		{
			var changed = await RunSweepAsync(cancellationToken).ConfigureAwait(false);
			_logger.LogInformation("NuGet version sweep complete; {Changed} package version(s) changed.", changed);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// A failed sweep must never take the service down with it: the next interval is a free
			// retry, and the last good snapshot is still on disk and still gating.
			_logger.LogError(ex, "NuGet version sweep failed; the previous snapshot stands until the next sweep.");
		}
	}
}
