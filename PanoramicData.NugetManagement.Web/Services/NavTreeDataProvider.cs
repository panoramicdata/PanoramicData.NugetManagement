using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
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
	private readonly string _organizationName;

	/// <summary>
	/// Initialises a new instance of the <see cref="NavTreeDataProvider"/> class.
	/// </summary>
	public NavTreeDataProvider(DashboardCacheService cache, IOptions<AppSettings> settings)
	{
		_cache = cache;
		_organizationName = settings.Value.NuGetOrganization;
	}

	/// <summary>
	/// Gets or sets an optional regex used to filter package nodes by name.
	/// When set, only packages whose <c>PackageId</c> matches are included in the tree.
	/// </summary>
	public Regex? FilterRegex { get; set; }

	/// <summary>
	/// When true, only locally-cloned repositories are included in the tree.
	/// </summary>
	public bool LocalOnly { get; set; }

	/// <summary>
	/// When true, the dashboard data is still being discovered/assessed: show a busy
	/// spinner on the org node and a "loading" placeholder under Repositories if it is empty.
	/// </summary>
	public bool IsLoading { get; set; }

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

		// Apply filters to determine which packages are visible
		var visibleRows = rows;
		if (visibleRows is not null && filter is not null)
		{
			visibleRows = [.. visibleRows.Where(r => filter.IsMatch(r.PackageId))];
		}

		if (visibleRows is not null && LocalOnly)
		{
			visibleRows = [.. visibleRows.Where(r => r.IsClonedLocally)];
		}

		// Calculate overall health for root node based on visible packages
		var totalIssues = visibleRows?.Sum(r => r.TotalFailures) ?? 0;
		var hasAnyErrors = visibleRows?.Any(r => r.TotalCriticals > 0 || r.TotalErrors > 0) == true;
		var hasAnyWarnings = visibleRows?.Any(r => r.TotalWarnings > 0) == true;

		var items = new List<NavItem>
		{
			// Top level: Organisations
			new() {
				Key = "organisations",
				Text = "Organisations",
				IconCss = "fas fa-sitemap",
				View = NavView.Dashboard,
				IsLeaf = false
			},

			// The organisation
			new() {
				Key = "root",
				Text = _organizationName,
				ParentKey = "organisations",
				IconCss = "fas fa-people-group",
				View = NavView.Dashboard,
				IsLeaf = false,
				IssueCount = totalIssues,
				HasErrors = hasAnyErrors,
				HasWarnings = hasAnyWarnings,
				IsBusy = IsLoading
			},

			// Repositories grouping — packages live under here
			new() {
				Key = "repositories",
				Text = "Repositories",
				ParentKey = "root",
				IconCss = "fas fa-cubes",
				View = NavView.Dashboard,
				IsLeaf = false
			},

			// Package updates management (also directly under the org)
			new() {
				Key = "nuget-updates",
				Text = "Package Updates",
				ParentKey = "root",
				IconCss = "fas fa-arrow-circle-up",
				View = NavView.NuGetUpdates,
				IsLeaf = true
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

				// Apply local-only filter
				if (LocalOnly && !row.IsClonedLocally)
				{
					continue;
				}
				var pkgKey = $"pkg:{row.PackageId}";
				var pkgIssues = row.TotalFailures;
				var pkgHasErrors = row.TotalCriticals > 0 || row.TotalErrors > 0;
				var pkgHasWarnings = row.TotalWarnings > 0;

				// Determine RAG icon for the package using shared row health state.
				var pkgIcon = GetPackageHealthIcon(row.HealthStatus);
				if (row.IsClonedLocally)
				{
					pkgIcon += " tree-node-local";

					// Colour the branch glyph by sync state: amber if out of sync, muted if unknown.
					if (row.IsSyncedWithOrigin == false)
					{
						pkgIcon += " tree-node-out-of-sync";
					}
					else if (row.IsSyncedWithOrigin is null)
					{
						pkgIcon += " tree-node-sync-unknown";
					}
				}

				if (row.IsWorkingTreeClean == false)
				{
					pkgIcon += " tree-node-dirty";
				}

				items.Add(new NavItem
				{
					Key = pkgKey,
					Text = row.PackageId,
					ParentKey = "repositories",
					IconCss = pkgIcon,
					View = NavView.PackageDetail,
					PackageId = row.PackageId,
					IsLeaf = row.Assessment is null,
					IssueCount = pkgIssues,
					HasErrors = pkgHasErrors,
					HasWarnings = pkgHasWarnings,
					IsWorkingTreeDirty = row.IsWorkingTreeClean == false
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
						var catHasErrors = catFailures.Any(r => r.Severity is AssessmentSeverity.Critical or AssessmentSeverity.Error);
						var catHasWarnings = catFailures.Any(r => r.Severity == AssessmentSeverity.Warning);

						items.Add(new NavItem
						{
							Key = catKey,
							Text = category.ToString(),
							ParentKey = pkgKey,
							IconCss = GetHealthIcon(true, catFailures.Count, catHasErrors, catHasWarnings),
							View = NavView.CategoryDetail,
							PackageId = row.PackageId,
							Category = category,
							IsLeaf = catFailures.Count == 0,
							IssueCount = catFailures.Count,
							HasErrors = catHasErrors,
							HasWarnings = catHasWarnings
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
								HasErrors = rule.Severity is AssessmentSeverity.Critical or AssessmentSeverity.Error,
								HasWarnings = rule.Severity == AssessmentSeverity.Warning
							});
						}
					}
				}
			}
		}

		// While loading, show a placeholder under Repositories if no repos are available yet.
		if (IsLoading && !items.Any(i => i.ParentKey == "repositories"))
		{
			items.Add(new NavItem
			{
				Key = "repositories-loading",
				Text = "Loading repositories...",
				ParentKey = "repositories",
				IconCss = "fas fa-spinner fa-spin",
				View = NavView.None,
				IsLeaf = true
			});
		}

		return items;
	}

	/// <summary>
	/// Returns a Font Awesome icon class for a rule based on its severity.
	/// </summary>
	private static string GetRuleIcon(AssessmentSeverity severity) => severity switch
	{
		AssessmentSeverity.Critical => "fas fa-skull-crossbones text-danger",
		AssessmentSeverity.Error => "fas fa-times-circle text-danger",
		AssessmentSeverity.Warning => "fas fa-exclamation-triangle text-warning",
		_ => "fas fa-info-circle text-info"
	};

	/// <summary>
	/// Returns a Font Awesome icon class with a RAG health indicator colour class.
	/// Error → red, Warning → orange, Info-only → blue, Clean → green.
	/// </summary>
	private static string GetHealthIcon(bool isAssessed, int issueCount, bool hasErrors, bool hasWarnings)
	{
		if (!isAssessed)
		{
			return "fas fa-spinner fa-spin text-muted";
		}

		if (issueCount == 0)
		{
			return "fas fa-cube text-success";
		}

		if (hasErrors)
		{
			return "fas fa-cube text-danger";
		}

		return hasWarnings
			? "fas fa-cube text-warning"
			: "fas fa-cube text-info";
	}

	private static string GetPackageHealthIcon(PackageHealthStatus healthStatus) => healthStatus switch
	{
		PackageHealthStatus.Pending => "fas fa-spinner fa-spin text-muted",
		PackageHealthStatus.Success => "fas fa-cube text-success",
		PackageHealthStatus.Error => "fas fa-cube text-danger",
		PackageHealthStatus.Warning => "fas fa-cube text-warning",
		PackageHealthStatus.Unknown => "fas fa-cube text-muted",
		_ => "fas fa-cube text-info"
	};
}
