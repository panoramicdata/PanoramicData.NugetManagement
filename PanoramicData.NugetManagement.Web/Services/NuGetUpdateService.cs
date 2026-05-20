using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Scans all assessed repositories for available NuGet package updates
/// and groups them by (package, current version, latest version).
/// </summary>
public class NuGetUpdateService
{
	private readonly DashboardCacheService _cache;
	private readonly ILogger<NuGetUpdateService> _logger;

	private List<NuGetUpdateGroup>? _groups;
	private DateTimeOffset? _lastCheckedUtc;
	private bool _isChecking;

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetUpdateService"/> class.
	/// </summary>
	public NuGetUpdateService(DashboardCacheService cache, ILogger<NuGetUpdateService> logger)
	{
		_cache = cache;
		_logger = logger;
	}

	/// <summary>
	/// Whether an update scan is currently in progress.
	/// </summary>
	public bool IsChecking => _isChecking;

	/// <summary>
	/// The UTC time of the last completed scan, or null if never run.
	/// </summary>
	public DateTimeOffset? LastCheckedUtc => _lastCheckedUtc;

	/// <summary>
	/// The current list of update groups, or null if no scan has been performed yet.
	/// </summary>
	public IReadOnlyList<NuGetUpdateGroup>? Groups => _groups;

	/// <summary>
	/// Scans all cached repository assessments for available NuGet package updates.
	/// Reads <c>Directory.Packages.props</c> content from each repository context,
	/// queries NuGet.org for the latest versions, and builds grouped results.
	/// </summary>
	/// <remarks>
	/// TODO: Wire up real implementation:
	///   1. Extend <see cref="PackageDashboardRow"/> to store parsed package versions
	///      from <c>Directory.Packages.props</c> (populated during assessment).
	///   2. For each distinct package ID found across all repos, query NuGet.org
	///      for the latest stable version (reuse the <c>PackageSearchResource</c>
	///      approach already used in <see cref="NuGetDiscoveryService"/>).
	///   3. Group by (PackageId, CurrentVersion, LatestVersion) where versions differ.
	/// </remarks>
	public Task CheckForUpdatesAsync(
		Action? onStateChanged = null,
		CancellationToken cancellationToken = default)
	{
		if (_isChecking)
		{
			return Task.CompletedTask;
		}

		_isChecking = true;
		onStateChanged?.Invoke();

		// TODO: implement the real scan (see remarks above).
		_logger.LogInformation("NuGet update scan not yet implemented.");

		_groups = [];
		_lastCheckedUtc = DateTimeOffset.UtcNow;
		_isChecking = false;
		onStateChanged?.Invoke();

		return Task.CompletedTask;
	}
}
