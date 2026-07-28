using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PanoramicData.Blazor;
using PanoramicData.Blazor.Models;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Provides <see cref="NavItem"/> data for the PDTree sidebar navigation.
/// Builds the tree from the dashboard cache down to individual rule issues.
/// </summary>
public class NavTreeDataProvider : DataProviderBase<NavItem>
{
	private readonly DashboardCacheService _cache;
	private readonly RuntimeSettingsService _runtimeSettings;
	private readonly string _configuredOrganizationName;
	private readonly ILogger<NavTreeDataProvider>? _logger;

	/// <summary>
	/// Initialises a new instance of the <see cref="NavTreeDataProvider"/> class.
	/// </summary>
	public NavTreeDataProvider(
		DashboardCacheService cache,
		RuntimeSettingsService runtimeSettings,
		IOptions<AppSettings> settings,
		ILogger<NavTreeDataProvider>? logger = null)
	{
		_cache = cache;
		_runtimeSettings = runtimeSettings;
		_configuredOrganizationName = settings.Value.NuGetOrganization;
		_logger = logger;
	}

	/// <summary>
	/// Builds the tree node key for an organisation. Keys must be namespaced per organisation:
	/// PDTree throws on a duplicate key and swallows the exception, so a collision renders the whole
	/// tree empty with nothing logged to the browser console.
	/// </summary>
	public static string OrgKey(string organization) => $"org:{organization}";

	/// <summary>Builds the key for an organisation's "Repositories" container.</summary>
	public static string ReposKey(string organization) => $"repos:{organization}";

	/// <summary>Builds the key for an organisation's "Issues" branch.</summary>
	public static string IssuesKey(string organization) => $"issues:{organization}";

	/// <summary>Builds the key for an organisation's "Package Updates" node.</summary>
	public static string UpdatesKey(string organization) => $"updates:{organization}";

	/// <summary>
	/// The key of the always-expanded top-level container. Unlike the other container nodes it is
	/// selectable, because selecting it shows the estate overview.
	/// </summary>
	public const string OrganisationsKey = "organisations";

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
	/// Tree structure: Organisations → organisation → { Repositories → package → category → rule,
	/// Issues → category → rule, Package Updates }.
	/// </summary>
	public List<NavItem> BuildNavItems()
	{
		var rows = _cache.GetCachedRows();
		var organizations = _runtimeSettings.Organizations;

		// Rows cached before multi-organisation support carry no organisation, so attribute them to
		// the first configured organisation rather than letting them vanish from the tree until the
		// next refresh.
		var fallbackOrganization = organizations.Count > 0 ? organizations[0] : _configuredOrganizationName;

		var items = new List<NavItem>
		{
			// Selecting Organisations shows the estate overview: it is the one node whose scope is every
			// organisation, so the aggregate figures belong to it rather than to a separate node.
			new()
			{
				Key = OrganisationsKey,
				Text = "Organisations",
				IconCss = "fas fa-sitemap",
				View = NavView.Dashboard,
				IsLeaf = false
			}
		};

		for (var orgIndex = 0; orgIndex < organizations.Count; orgIndex++)
		{
			AddOrganizationNodes(items, organizations[orgIndex], orgIndex, rows, fallbackOrganization);
		}

		return DeduplicateKeys(items);
	}

	/// <summary>
	/// Adds one organisation's whole subtree: the organisation node, its Repositories branch (with
	/// packages, categories and failing rules), its Issues branch and its Package Updates node.
	/// </summary>
	private void AddOrganizationNodes(
		List<NavItem> items,
		string organization,
		int orgIndex,
		List<PackageDashboardRow>? rows,
		string fallbackOrganization)
	{
		var orgKey = OrgKey(organization);
		var reposKey = ReposKey(organization);
		var issuesKey = IssuesKey(organization);

		var orgRows = rows?
			.Where(r => BelongsToOrganization(r, organization, fallbackOrganization))
			.ToList();

		var visibleRows = ApplyFilters(orgRows);

		// While a filter is active, an organisation with nothing matching is omitted entirely rather
		// than left as an empty branch to open and find nothing in. Only while filtering: an
		// organisation with genuinely no packages must stay visible when unfiltered, or there would be
		// no way to reach its settings — including the button that removes it.
		var isFiltering = FilterRegex is not null || LocalOnly;
		if (isFiltering && (visibleRows is null || visibleRows.Count == 0))
		{
			return;
		}

		var totalIssues = visibleRows?.Sum(r => r.TotalFailures) ?? 0;
		var hasAnyErrors = visibleRows?.Any(r => r.TotalCriticals > 0 || r.TotalErrors > 0) == true;
		var hasAnyWarnings = visibleRows?.Any(r => r.TotalWarnings > 0) == true;

		items.Add(new NavItem
		{
			Key = orgKey,
			Text = organization,
			ParentKey = OrganisationsKey,
			IconCss = "fas fa-people-group",
			View = NavView.Home,
			Organization = organization,
			IsLeaf = false,
			SortOrder = orgIndex,
			IssueCount = totalIssues,
			HasErrors = hasAnyErrors,
			HasWarnings = hasAnyWarnings,
			IsBusy = IsLoading
		});

		items.Add(new NavItem
		{
			Key = reposKey,
			Text = "Repositories",
			ParentKey = orgKey,
			IconCss = "fas fa-cubes",
			View = NavView.Home,
			Organization = organization,
			IsLeaf = false,
			SortOrder = 0
		});

		AddIssueHierarchy(items, organization, issuesKey, orgKey, visibleRows);

		items.Add(new NavItem
		{
			Key = UpdatesKey(organization),
			Text = "Package Updates",
			ParentKey = orgKey,
			IconCss = "fas fa-arrow-circle-up",
			View = NavView.NuGetUpdates,
			Organization = organization,
			IsLeaf = true,
			SortOrder = 2
		});

		AddPackageNodes(items, organization, reposKey, visibleRows);

		// While loading, show a placeholder under Repositories if no repos are available yet.
		if (IsLoading && (visibleRows is null || visibleRows.Count == 0))
		{
			items.Add(new NavItem
			{
				Key = $"repos-loading:{organization}",
				Text = "Loading repositories...",
				ParentKey = reposKey,
				IconCss = "fas fa-spinner fa-spin",
				View = NavView.None,
				Organization = organization,
				IsLeaf = true
			});
		}
	}

	/// <summary>
	/// Adds the package → category → rule branch for one organisation.
	/// </summary>
	private static void AddPackageNodes(
		List<NavItem> items,
		string organization,
		string reposKey,
		List<PackageDashboardRow>? visibleRows)
	{
		if (visibleRows is null)
		{
			return;
		}

		foreach (var row in visibleRows.OrderBy(r => r.PackageId, StringComparer.OrdinalIgnoreCase))
		{
			var pkgKey = $"pkg:{organization}:{row.PackageId}";
			var pkgIssues = row.TotalFailures;
			var pkgHasErrors = row.TotalCriticals > 0 || row.TotalErrors > 0;
			var pkgHasWarnings = row.TotalWarnings > 0;
			var pkgIcon = BuildPackageIconCss(row);

			items.Add(new NavItem
			{
				Key = pkgKey,
				Text = row.PackageId,
				ParentKey = reposKey,
				IconCss = pkgIcon,
				View = NavView.PackageDetail,
				Organization = organization,
				PackageId = row.PackageId,
				IsLeaf = row.Assessment is null,
				IssueCount = pkgIssues,
				HasErrors = pkgHasErrors,
				HasWarnings = pkgHasWarnings,
				IsWorkingTreeDirty = row.IsWorkingTreeClean == false
			});

			// Category sub-nodes (only if assessed)
			if (row.Assessment is null)
			{
				continue;
			}

			foreach (var category in row.CategorySummaries.Keys.OrderBy(c => c.ToString()))
			{
				var catKey = $"cat:{organization}:{row.PackageId}:{category}";
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
					Organization = organization,
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
						Key = $"rule:{organization}:{row.PackageId}:{rule.RuleId}",
						Text = $"{rule.RuleId} {rule.RuleName}",
						ParentKey = catKey,
						IconCss = GetRuleIcon(rule.Severity),
						View = NavView.RuleDetail,
						Organization = organization,
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

	/// <summary>
	/// Adds the Issues branch for one organisation: category → rule, the "dimensional flip" of the
	/// Repositories branch. It deliberately stops at rule level — a rule can affect every repository
	/// in the organisation, so listing repositories here would add thousands of sidebar nodes. The
	/// affected repositories and the bulk actions belong in the centre panel, which has room for
	/// per-repository checkboxes.
	/// </summary>
	private static void AddIssueHierarchy(
		List<NavItem> items,
		string organization,
		string issuesKey,
		string orgKey,
		List<PackageDashboardRow>? visibleRows)
	{
		var assessed = visibleRows?
			.Where(r => r.Assessment is not null)
			.Select(r => new AssessedPackage(
				r.RepositoryFullName ?? r.PackageId,
				r.Assessment!,
				r.PackageId))
			.ToList();

		var view = assessed is { Count: > 0 }
			? IssueCentricViewBuilder.Build(assessed)
			: null;

		var categories = view?.Categories ?? [];

		items.Add(new NavItem
		{
			Key = issuesKey,
			Text = "Issues",
			ParentKey = orgKey,
			IconCss = "fas fa-layer-group",
			View = NavView.Issues,
			Organization = organization,
			IsLeaf = categories.Count == 0,
			SortOrder = 1,
			IssueCount = categories.Sum(c => c.IssueClasses.Sum(i => i.AffectedRepositoryCount))
		});

		for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
		{
			var category = categories[categoryIndex];
			var categoryKey = $"icat:{organization}:{category.Category}";

			items.Add(new NavItem
			{
				Key = categoryKey,
				Text = category.Category.ToString(),
				ParentKey = issuesKey,
				IconCss = GetIssueSeverityIcon(category.Severity),
				View = NavView.IssueCategoryDetail,
				Organization = organization,
				Category = category.Category,
				IsLeaf = category.IssueClasses.Count == 0,
				SortOrder = categoryIndex,
				IssueCount = category.IssueClasses.Count,
				AffectedRepoCount = category.AffectedRepositoryCount,
				HasErrors = category.Severity is AssessmentSeverity.Critical or AssessmentSeverity.Error,
				HasWarnings = category.Severity == AssessmentSeverity.Warning
			});

			for (var ruleIndex = 0; ruleIndex < category.IssueClasses.Count; ruleIndex++)
			{
				var issueClass = category.IssueClasses[ruleIndex];

				items.Add(new NavItem
				{
					Key = $"irule:{organization}:{category.Category}:{issueClass.RuleId}",
					Text = $"{issueClass.RuleId} {issueClass.RuleName}",
					ParentKey = categoryKey,
					IconCss = GetRuleIcon(issueClass.Severity),
					View = NavView.IssueRuleDetail,
					Organization = organization,
					Category = category.Category,
					RuleId = issueClass.RuleId,
					IsLeaf = true,
					SortOrder = ruleIndex,
					IssueCount = issueClass.AffectedRepositoryCount,
					AffectedRepoCount = issueClass.AffectedRepositoryCount,
					HasErrors = issueClass.Severity is AssessmentSeverity.Critical or AssessmentSeverity.Error,
					HasWarnings = issueClass.Severity == AssessmentSeverity.Warning
				});
			}
		}
	}

	/// <summary>
	/// Applies the package-name regex and locally-cloned filters.
	/// </summary>
	private List<PackageDashboardRow>? ApplyFilters(List<PackageDashboardRow>? rows)
	{
		if (rows is null)
		{
			return null;
		}

		var filtered = rows.AsEnumerable();

		if (FilterRegex is not null)
		{
			filtered = filtered.Where(r => FilterRegex.IsMatch(r.PackageId));
		}

		if (LocalOnly)
		{
			filtered = filtered.Where(r => r.IsClonedLocally);
		}

		return [.. filtered];
	}

	/// <summary>
	/// Decides whether a cached row belongs to the given organisation. Rows cached before
	/// multi-organisation support carry no organisation and are attributed to the fallback.
	/// </summary>
	private static bool BelongsToOrganization(PackageDashboardRow row, string organization, string fallbackOrganization)
		=> string.IsNullOrEmpty(row.Organization)
			? string.Equals(organization, fallbackOrganization, StringComparison.OrdinalIgnoreCase)
			: string.Equals(row.Organization, organization, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Drops any node whose key repeats one already present. PDTree builds an internal dictionary
	/// keyed on the key field and throws on a duplicate, and it swallows that exception — the visible
	/// symptom is an entirely empty tree with a clean console, which is near-impossible to diagnose
	/// from the UI. Losing a node is far preferable, and the loss is logged.
	/// </summary>
	private List<NavItem> DeduplicateKeys(List<NavItem> items)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var result = new List<NavItem>(items.Count);

		foreach (var item in items)
		{
			if (seen.Add(item.Key))
			{
				result.Add(item);
				continue;
			}

			_logger?.LogWarning(
				"Dropped navigation node with duplicate key '{Key}' (text '{Text}'). The tree would " +
				"otherwise render empty. This indicates a key-namespacing bug.",
				item.Key,
				item.Text);
		}

		return result;
	}

	/// <summary>
	/// Compares two sibling nodes for PDTree's Sort parameter: explicit rank first, then text.
	/// Without this PDTree orders children alphabetically, which would list Critical below Info.
	/// </summary>
	public static int CompareNavItems(NavItem left, NavItem right)
	{
		var byOrder = left.SortOrder.CompareTo(right.SortOrder);
		return byOrder != 0
			? byOrder
			: string.Compare(left.Text, right.Text, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Builds the full icon class for a package node: its RAG health glyph plus the local-clone,
	/// sync-state and dirty-working-tree modifiers.
	/// </summary>
	/// <remarks>
	/// Public and static because the navigation tree's node template also calls it, to resolve a
	/// package's icon from live row state during an assessment. That lets a repository's spinner be
	/// replaced by its result as soon as that result lands, without rebuilding the tree — rebuilding
	/// nodes mid-run is what previously made the tree flicker and the scrollbar jump.
	/// </remarks>
	public static string BuildPackageIconCss(PackageDashboardRow row)
	{
		var icon = GetPackageHealthIcon(row.HealthStatus);

		if (row.IsClonedLocally)
		{
			icon += " tree-node-local";

			// Colour the branch glyph by sync state: amber if out of sync, muted if unknown.
			if (row.IsSyncedWithOrigin == false)
			{
				icon += " tree-node-out-of-sync";
			}
			else if (row.IsSyncedWithOrigin is null)
			{
				icon += " tree-node-sync-unknown";
			}
		}

		if (row.IsWorkingTreeClean == false)
		{
			icon += " tree-node-dirty";
		}

		return icon;
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
	/// Returns the icon for an issue-hierarchy category, coloured by its highest severity.
	/// </summary>
	private static string GetIssueSeverityIcon(AssessmentSeverity severity) => severity switch
	{
		AssessmentSeverity.Critical => "fas fa-layer-group text-danger",
		AssessmentSeverity.Error => "fas fa-layer-group text-danger",
		AssessmentSeverity.Warning => "fas fa-layer-group text-warning",
		_ => "fas fa-layer-group text-info"
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
