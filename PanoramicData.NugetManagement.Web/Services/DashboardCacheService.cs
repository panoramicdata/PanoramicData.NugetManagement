using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Singleton in-memory cache for dashboard state.
/// Since this is a single-user local tool, the cache lives server-side in memory.
/// </summary>
public class DashboardCacheService
{
	private readonly Lock _lock = new();
	private List<PackageDashboardRow>? _cachedRows;
	private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;

	/// <summary>
	/// The duration after which the cache is considered stale and should be refreshed.
	/// </summary>
	public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

	/// <summary>
	/// Gets the cached dashboard rows, or null if no cache exists.
	/// </summary>
	public List<PackageDashboardRow>? GetCachedRows()
	{
		lock (_lock)
		{
			return _cachedRows;
		}
	}

	/// <summary>
	/// Gets the UTC time of the last successful refresh.
	/// </summary>
	public DateTimeOffset LastRefreshUtc
	{
		get
		{
			lock (_lock)
			{
				return _lastRefreshUtc;
			}
		}
	}

	/// <summary>
	/// Whether the cache is stale and needs refreshing.
	/// </summary>
	public bool IsStale
	{
		get
		{
			lock (_lock)
			{
				return _cachedRows is null || DateTimeOffset.UtcNow - _lastRefreshUtc > CacheDuration;
			}
		}
	}

	/// <summary>
	/// Updates the cached rows and refresh timestamp.
	/// Called when a full refresh cycle completes.
	/// </summary>
	public void Update(List<PackageDashboardRow> rows)
	{
		lock (_lock)
		{
			_cachedRows = rows;
			_lastRefreshUtc = DateTimeOffset.UtcNow;
		}
	}

	/// <summary>
	/// Sets the cached rows without updating the refresh timestamp.
	/// Used for incremental updates (e.g. after discovering packages but before full assessment).
	/// </summary>
	public void SetRows(List<PackageDashboardRow> rows)
	{
		lock (_lock)
		{
			_cachedRows = rows;
		}
	}

	/// <summary>
	/// Gets a single cached row by package ID.
	/// </summary>
	public PackageDashboardRow? GetRow(string packageId)
	{
		lock (_lock)
		{
			return _cachedRows?.FirstOrDefault(r =>
				string.Equals(r.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
		}
	}

	/// <summary>
	/// Notifies that a single row's assessment was updated (refreshes the timestamp).
	/// </summary>
	public void NotifyRowUpdated()
	{
		lock (_lock)
		{
			_lastRefreshUtc = DateTimeOffset.UtcNow;
		}
	}
}
