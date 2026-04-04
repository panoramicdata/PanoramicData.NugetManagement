using PanoramicData.Blazor;
using PanoramicData.Blazor.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Data provider that feeds cached dashboard rows to PDTable with filtering, sorting, and paging.
/// </summary>
public class PackageDashboardDataProvider : DataProviderBase<PackageDashboardRow>
{
	private readonly DashboardCacheService _cache;

	/// <summary>
	/// Initializes a new instance of the <see cref="PackageDashboardDataProvider"/> class.
	/// </summary>
	public PackageDashboardDataProvider(DashboardCacheService cache)
	{
		_cache = cache;
	}

	/// <summary>
	/// Returns the current set of dashboard rows, applying search, filter, sort, and paging.
	/// </summary>
	public override Task<DataResponse<PackageDashboardRow>> GetDataAsync(DataRequest<PackageDashboardRow> request, CancellationToken cancellationToken)
	{
		var query = (_cache.GetCachedRows() ?? []).AsQueryable();

		// Apply PDTable search text (free-text filter across PackageId, RepositoryFullName, LatestVersion)
		if (!string.IsNullOrWhiteSpace(request.SearchText))
		{
			var search = request.SearchText.Trim();
			query = query.Where(r =>
				r.PackageId.Contains(search, StringComparison.OrdinalIgnoreCase)
				|| (r.RepositoryFullName != null && r.RepositoryFullName.Contains(search, StringComparison.OrdinalIgnoreCase))
				|| (r.LatestVersion != null && r.LatestVersion.Contains(search, StringComparison.OrdinalIgnoreCase)));
		}

		var totalCount = query.Count();

		// Apply sorting
		if (request.SortFieldExpression is not null)
		{
			query = request.SortDirection == SortDirection.Descending
				? query.OrderByDescending(request.SortFieldExpression)
				: query.OrderBy(request.SortFieldExpression);
		}

		// Apply paging
		if (request.Skip.HasValue)
		{
			query = query.Skip(request.Skip.Value);
		}

		if (request.Take.HasValue)
		{
			query = query.Take(request.Take.Value);
		}

		var items = query.ToList();
		return Task.FromResult(new DataResponse<PackageDashboardRow>(items, totalCount));
	}
}
