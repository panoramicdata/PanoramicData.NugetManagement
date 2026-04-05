using System.Text.RegularExpressions;
using PanoramicData.Blazor;
using PanoramicData.Blazor.Models;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Provides <see cref="NavItem"/> data for the PDTree sidebar navigation.
/// Builds the tree from the dashboard cache down to individual rule issues.
/// </summary>
public class NavTreeDataProvider : DataProviderBase<NavItem>
{
	private readonly DashboardCacheService _cache;

	/// <summary>
	/// Initialises a new instance of the <see cref="NavTreeDataProvider"/> class.
	/// </summary>
	public NavTreeDataProvider(DashboardCacheService cache)
	{
		_cache = cache;
	}

	/// <summary>
	/// Gets or sets an optional regex used to filter package nodes by name.
	/// When set, only packages whose <c>PackageId</c> matches are included in the tree.
	/// </summary>
	public Regex? FilterRegex { get; set; }

	/// <inheritdoc />
	public override Task<DataResponse<NavItem>> GetDataAsync(DataRequest<NavItem> request, CancellationToken cancellationToken)
	{
		var items = BuildNavItems();
		return Task.FromResult(new DataResponse<NavItem>(items, items.Count));
	}

	/// <summary>
	/// Builds the full list of navigation items from the current cache state.
	/// Tree structure: Dashboard → packages → categories → individual failing rules.
	/// </summary>
	public List<NavItem> BuildNavItems()
	{
		var rows = _cache.GetCachedRows();
		var filter = FilterRegex;

		// Apply filter to determine which packages are visible
		var visibleRows = rows;
		if (visibleRows is not null && filter is not null)
		{
			visibleRows = [.. visibleRows.Where(r => filter.IsMatch(r.PackageId))];
		}

		// Calculate overall health for root node based on visible packages
		var totalIssues = visibleRows?.Sum(r => r.TotalFailures) ?? 0;
		var hasAnyErrors = visibleRows?.Any(r => r.TotalErrors > 0) == true;

		var items = new List<NavItem>
		{
			// Root: Dashboard
			new() {
				Key = "root",
				Text = "Dashboard",
				IconCss = "fas fa-tachometer-alt",
				View = NavView.Dashboard,
				IsLeaf = false,
				IssueCount = totalIssues,
				HasErrors = hasAnyErrors
			}
		};

		// Package nodes
		if (rows is not null)
		{
			foreach (var row in rows.OrderBy(r => r.PackageId, StringComparer.OrdinalIgnoreCase))
			{
				// Apply regex filter on package name
				if (filter is not null && !filter.IsMatch(row.PackageId))
				{
					continue;
				}
				var pkgKey = $"pkg:{row.PackageId}";
				var pkgIssues = row.TotalFailures;
				var pkgHasErrors = row.TotalErrors > 0;

				// Determine RAG icon for the package
				var pkgIcon = GetHealthIcon(row.Assessment is not null, pkgIssues, pkgHasErrors);

				items.Add(new NavItem
				{
					Key = pkgKey,
					Text = row.PackageId,
					ParentKey = "root",
					IconCss = pkgIcon,
					View = NavView.PackageDetail,
					PackageId = row.PackageId,
					IsLeaf = row.Assessment is null,
					IssueCount = pkgIssues,
					HasErrors = pkgHasErrors
				});

				// Category sub-nodes (only if assessed)
				if (row.Assessment is not null)
				{
					foreach (var category in row.CategorySummaries.Keys.OrderBy(c => c.ToString()))
					{
						var catKey = $"cat:{row.PackageId}:{category}";
						var catFailures = row.Assessment.RuleResults
							.Where(r => !r.Passed && r.Category == category)
							.ToList();
						var catHasErrors = catFailures.Any(r => r.Severity == AssessmentSeverity.Error);

						items.Add(new NavItem
						{
							Key = catKey,
							Text = category.ToString(),
							ParentKey = pkgKey,
							IconCss = GetHealthIcon(true, catFailures.Count, catHasErrors),
							View = NavView.CategoryDetail,
							PackageId = row.PackageId,
							Category = category,
							IsLeaf = catFailures.Count == 0,
							IssueCount = catFailures.Count,
							HasErrors = catHasErrors
						});

						// Individual failing rule nodes under each category
						foreach (var rule in catFailures.OrderBy(r => r.RuleId))
						{
							items.Add(new NavItem
							{
								Key = $"rule:{row.PackageId}:{rule.RuleId}",
								Text = $"{rule.RuleId} {rule.RuleName}",
								ParentKey = catKey,
								IconCss = GetRuleIcon(rule.Severity),
								View = NavView.RuleDetail,
								PackageId = row.PackageId,
								Category = category,
								RuleId = rule.RuleId,
								IsLeaf = true,
								IssueCount = 1,
								HasErrors = rule.Severity == AssessmentSeverity.Error
							});
						}
					}
				}
			}
		}

		return items;
	}

	/// <summary>
	/// Returns a Font Awesome icon class for a rule based on its severity.
	/// </summary>
	private static string GetRuleIcon(AssessmentSeverity severity) => severity switch
	{
		AssessmentSeverity.Error => "fas fa-times-circle text-danger",
		AssessmentSeverity.Warning => "fas fa-exclamation-triangle text-warning",
		_ => "fas fa-info-circle text-info"
	};

	/// <summary>
	/// Returns a Font Awesome icon class with a RAG health indicator colour class.
	/// </summary>
	private static string GetHealthIcon(bool isAssessed, int issueCount, bool hasErrors)
	{
		if (!isAssessed)
		{
			return "fas fa-spinner fa-spin text-muted";
		}

		if (issueCount == 0)
		{
			return "fas fa-cube text-success";
		}

		return hasErrors
			? "fas fa-cube text-danger"
			: "fas fa-cube text-warning";
	}
}
