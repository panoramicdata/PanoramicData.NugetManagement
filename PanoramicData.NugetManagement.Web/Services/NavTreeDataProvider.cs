using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PanoramicData.Blazor;
using PanoramicData.Blazor.Models;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Provides <see cref="NavItem"/> data for the PDTree sidebar navigation.
/// Builds the tree from the dashboard cache down to individual rule issues.
/// </summary>
public partial class NavTreeDataProvider : DataProviderBase<NavItem>
{
	private readonly DashboardCacheService _cache;
	private readonly RegressionGuardService? _regressionGuard;
	private readonly WorkLaneService? _workLanes;
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
		RegressionGuardService? regressionGuard,
		WorkLaneService? workLanes = null,
		ILogger<NavTreeDataProvider>? logger = null)
	{
		_cache = cache;
		_regressionGuard = regressionGuard;
		_workLanes = workLanes;
		_runtimeSettings = runtimeSettings;
		_configuredOrganizationName = settings.Value.NuGetOrganization;
		_logger = logger;
	}

	/// <inheritdoc />
	public override Task<DataResponse<NavItem>> GetDataAsync(DataRequest<NavItem> request, CancellationToken cancellationToken)
	{
		var items = BuildNavItems();
		return Task.FromResult(new DataResponse<NavItem>(items, items.Count));
	}

	/// <summary>
	/// Builds the full list of navigation items from the current cache state.
	/// Tree structure: Organisations → organisation → { Repositories → repository →
	/// { Packages → package, category → rule }, Issues → category → rule, Not governed → package }.
	/// </summary>
	public List<NavItem> BuildNavItems()
	{
		var rows = _cache.GetCachedRows();
		var organizations = _runtimeSettings.Organizations;

		// Rows cached before multi-organisation support carry no organisation, so attribute them to
		// the first configured organisation rather than letting them vanish from the tree until the
		// next refresh.
		var fallbackOrganization = organizations.Count > 0 ? organizations[0] : _configuredOrganizationName;

		// The organisations are built first because the Organisations node is coloured by the worst of
		// them, and NavItem is immutable once created.
		var orgItems = new List<NavItem>();
		var orgStatuses = new List<PackageHealthStatus>();

		for (var orgIndex = 0; orgIndex < organizations.Count; orgIndex++)
		{
			var orgStatus = AddOrganizationNodes(orgItems, organizations[orgIndex], orgIndex, rows, fallbackOrganization);

			if (orgStatus is not null)
			{
				orgStatuses.Add(orgStatus.Value);
			}
		}

		var estateStatus = NavHealthRollup.Worst(orgStatuses);

		var items = new List<NavItem>
		{
			// Selecting Organisations shows the estate overview: it is the one node whose scope is every
			// organisation, so the aggregate figures belong to it rather than to a separate node.
			new()
			{
				Key = OrganisationsKey,
				Text = "Organisations",
				IconCss = NavHealthRollup.Icon("fas fa-sitemap", estateStatus),
				View = NavView.Dashboard,
				IsLeaf = false,
				HealthStatus = estateStatus
			}
		};

		items.AddRange(orgItems);

		return DeduplicateKeys(items);
	}

	/// <summary>
	/// Adds one organisation's whole subtree: the organisation node, its Repositories branch (with
	/// packages, categories and failing rules) and its Issues branch. Returns the organisation's rolled-up
	/// health so the Organisations node above can take the worst of them, or null when the organisation
	/// was filtered out entirely.
	/// </summary>
	private PackageHealthStatus? AddOrganizationNodes(
		List<NavItem> items,
		string organization,
		int orgIndex,
		List<RepositoryDashboardRow>? rows,
		string fallbackOrganization)
	{
		var orgKey = OrgKey(organization);
		var reposKey = ReposKey(organization);
		var issuesKey = IssuesKey(organization);

		var orgRows = rows?
			.Where(r => BelongsToOrganization(r, organization, fallbackOrganization))
			.ToList();

		// Packages whose repository is not ours are held apart from the estate entirely: they are not
		// assessed, so they have no health to roll up and no rules to answer for. They are accounted
		// for in their own branch rather than dropped, so a package cannot leave the tree unexplained.
		var ungovernedPackages = UngovernedPackagesFor(organization, fallbackOrganization);
		var visibleRows = ApplyFilters(orgRows?.Where(r => r.IsGoverned).ToList());

		// While a filter is active, an organisation with nothing matching is omitted entirely rather
		// than left as an empty branch to open and find nothing in. Only while filtering: an
		// organisation with genuinely no packages must stay visible when unfiltered, or there would be
		// no way to reach its settings — including the button that removes it.
		if (IsEmptyUnderFilter(visibleRows))
		{
			return null;
		}

		// Excluding a repository is a decision that it does not count, so it takes no part in any
		// figure or colour above it. It stays in visibleRows, and so stays in the tree dimmed, because
		// a repository that vanished when excluded could never be brought back. Note that IsGoverned
		// answers a different question — whether the nuspec names one of our organisations — and says
		// nothing about what we chose to exclude.
		var countedRows = visibleRows?
			.Where(r => !_runtimeSettings.IsRepositoryExcluded(r.RepositoryFullName))
			.ToList();

		var reposStatus = NavHealthRollup.ForRepositories(countedRows);

		// The Issues branch is built before the organisation node it hangs off, because that node is
		// coloured by the worst of the two branches and NavItem is immutable once created.
		var issuesItems = new List<NavItem>();
		var issuesStatus = AddIssueHierarchy(issuesItems, organization, issuesKey, orgKey, countedRows);

		var orgStatus = NavHealthRollup.Worst(reposStatus, issuesStatus);

		items.Add(OrganisationNode(organization, orgIndex, orgStatus, countedRows));

		items.Add(RepositoriesNode(organization, reposStatus));

		items.AddRange(issuesItems);

		AddWorkNodes(
			items,
			OrgWorkKey(organization),
			orgKey,
			$"org:{organization.ToLowerInvariant()}",
			organization,
			repositoryFullName: null,
			// Rank 2, after Repositories (0) and Issues (1) and before Not governed (3). Explicit
			// rather than tie-broken: today "Issues" happens to sort before "Work" alphabetically,
			// but that is luck, not a rule, and the same trap that buried the repo-level node.
			sortOrder: 2);

		AddRepositoryNodes(items, organization, reposKey, visibleRows);
		AddNotGovernedNodes(items, organization, orgKey, ungovernedPackages);

		AddLoadingPlaceholder(items, organization, reposKey, visibleRows);

		return orgStatus;
	}

	private bool IsEmptyUnderFilter(List<RepositoryDashboardRow>? visibleRows)
		=> (FilterRegex is not null || LocalOnly) && (visibleRows is null || visibleRows.Count == 0);

	private void AddLoadingPlaceholder(List<NavItem> items, string organization, string reposKey, List<RepositoryDashboardRow>? visibleRows)
	{
		// While loading, show a placeholder under Repositories if no repos are available yet.
		if (IsLoading && (visibleRows is null || visibleRows.Count == 0))
		{
			items.Add(LoadingNode(organization, reposKey));
		}
	}

	private NavItem OrganisationNode(
		string organization,
		int orgIndex,
		PackageHealthStatus orgStatus,
		List<RepositoryDashboardRow>? countedRows)
	{
		var totalIssues = countedRows?.Sum(r => r.TotalFailures) ?? 0;
		var hasAnyErrors = countedRows?.Any(r => r.TotalCriticals > 0 || r.TotalErrors > 0) == true;
		var hasAnyWarnings = countedRows?.Any(r => r.TotalWarnings > 0) == true;

		return new NavItem
		{
			Key = OrgKey(organization),
			Text = organization,
			ParentKey = OrganisationsKey,
			IconCss = NavHealthRollup.Icon("fas fa-people-group", orgStatus),
			View = NavView.Organisation,
			Organization = organization,
			IsLeaf = false,
			SortOrder = orgIndex,
			IssueCount = totalIssues,
			HasErrors = hasAnyErrors,
			HasWarnings = hasAnyWarnings,
			HealthStatus = orgStatus,
			IsBusy = IsLoading
		};
	}

	private static NavItem RepositoriesNode(string organization, PackageHealthStatus reposStatus)
		=> new()
		{
			Key = ReposKey(organization),
			Text = "Repositories",
			ParentKey = OrgKey(organization),
			IconCss = NavHealthRollup.Icon("fas fa-cubes", reposStatus),
			View = NavView.Repositories,
			Organization = organization,
			IsLeaf = false,
			SortOrder = 0,
			HealthStatus = reposStatus
		};

	private static NavItem LoadingNode(string organization, string reposKey)
		=> new()
		{
			Key = $"repos-loading:{organization}",
			Text = "Loading repositories...",
			ParentKey = reposKey,
			IconCss = "fas fa-spinner fa-spin",
			View = NavView.None,
			Organization = organization,
			IsLeaf = true
		};

	/// <summary>
	/// The repositories whose build-guard state the user should look at, keyed by repository full
	/// name. Verified, Queued and Building are absent: they are the states that need nobody.
	/// </summary>
	private Dictionary<string, GuardState> GuardStatesNeedingAttention()
		=> _regressionGuard is null
			? []
			: _regressionGuard.Statuses
				// Only a revert earns a mark on the tree. Everything else the guard does is an event —
				// queued, building, verified, a build that was already failing — and events belong in
				// the console, which narrates them as they happen. A revert is different in kind: it
				// means work the user did has been taken away, and that must still be visible once the
				// console has scrolled.
				.Where(status => status.State is GuardState.RegressionReverted)
				.GroupBy(status => status.RepositoryFullName, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(
					group => group.Key,
					group => group.OrderByDescending(status => status.UpdatedUtc).First().State,
					StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Adds the repository → { packages, category → rule } branch for one organisation.
	/// </summary>
	/// <remarks>
	/// The categories hang off the repository rather than off a package, because a repository is what
	/// the rules evaluate. While a package stood in for its repository, PanoramicData.ECharts — which
	/// publishes four — appeared four times, and the same findings were reported and remediated once
	/// per package.
	/// </remarks>
	/// <summary>
	/// The health a package's version-versus-tag comparison deserves.
	/// </summary>
	/// <remarks>
	/// Three states, because <see cref="PublishedPackage.MatchesTag"/> has three: in step is green,
	/// disagreeing is amber, and a missing version or tag is genuinely unknown and stays grey. Folding
	/// the first and the last together — which is what a plain "is it out of step?" boolean does — is
	/// what made a package sitting exactly at the tag look unassessed.
	/// </remarks>
	private static PackageHealthStatus PackageTagStatus(bool? matchesTag) => matchesTag switch
	{
		true => PackageHealthStatus.Success,
		false => PackageHealthStatus.Warning,
		_ => PackageHealthStatus.Unknown
	};

	private void AddRepositoryNodes(
		List<NavItem> items,
		string organization,
		string reposKey,
		List<RepositoryDashboardRow>? visibleRows)
	{
		if (visibleRows is null)
		{
			return;
		}

		var guardStates = GuardStatesNeedingAttention();

		foreach (var row in visibleRows.OrderBy(r => r.RepositoryFullName, StringComparer.OrdinalIgnoreCase))
		{
			AddRepositoryNode(items, organization, reposKey, row, guardStates);
		}
	}

	private void AddRepositoryNode(
		List<NavItem> items,
		string organization,
		string reposKey,
		RepositoryDashboardRow row,
		Dictionary<string, GuardState> guardStates)
	{
		var repoKey = RepoKey(row.RepositoryFullName);
		var repoLaneKey = RepositoryLaneKey(row.RepositoryFullName);

		AddRepositoryItem(items, organization, reposKey, row, guardStates);
		AddPackagesNode(items, organization, repoKey, row);

		AddWorkNodes(
			items,
			WorkKey(row.RepositoryFullName),
			repoKey,
			repoLaneKey,
			organization,
			row.RepositoryFullName,
			// Rank 0: first, above Packages (1), Issues (2) and the categories (3). Explicit rather
			// than tie-broken, because PDTree falls back to alphabetical order and "Work" loses that
			// race against every other name under a repository.
			sortOrder: 0);

		AddPackageNodes(items, organization, row);
		AddOpenIssueNodes(items, organization, repoKey, row);
		AddAssessmentNodes(items, organization, repoKey, row);
	}

	private void AddRepositoryItem(
		List<NavItem> items,
		string organization,
		string reposKey,
		RepositoryDashboardRow row,
		Dictionary<string, GuardState> guardStates)
	{
		var repoKey = RepoKey(row.RepositoryFullName);
		var repoLaneKey = RepositoryLaneKey(row.RepositoryFullName);
		var assessmentFailed = row.Assessment is null && row.Status == PackageStatus.Error;
		var repoIssues = row.TotalFailures;
		var repoHasErrors = row.TotalCriticals > 0 || row.TotalErrors > 0 || assessmentFailed;
		var repoHasWarnings = row.TotalWarnings > 0;

		items.Add(new NavItem
		{
			Key = repoKey,
			// The owner is already the organisation node above, and repeating it in every child
			// would spend the width the repository names need.
			Text = row.RepositoryName,
			ParentKey = reposKey,
			IconCss = BuildRepositoryIconCss(row),
			View = NavView.RepositoryDetail,
			Organization = organization,
			IsLeaf = false,
			IssueCount = repoIssues,
			HasErrors = repoHasErrors,
			HasWarnings = repoHasWarnings,
			IsWorkingTreeDirty = row.IsWorkingTreeClean == false,
			IsClonedLocally = row.IsClonedLocally,
			IsSyncedWithOrigin = row.IsSyncedWithOrigin,
			// Carried, never folded into the icon or the roll-up above: a repository that does not
			// build is not a repository that fails a rule.
			BuildState = row.LastBuildState,
			RepositoryFullName = row.RepositoryFullName,
			IsExcluded = _runtimeSettings.IsRepositoryExcluded(row.RepositoryFullName),
			// The spec asserts repository nodes already reflect their lane. They never did — in the
			// single-queue design IsBusy was rendered on the organisation node alone — and with no
			// estate-wide roll-up node this spinner is the only place activity on a repository is
			// visible without expanding its Work node. Running, not merely queued: a lane full of
			// pending items is waiting, not working, and spinning for it would make the whole tree
			// spin for the duration of a bulk action.
			IsBusy = IsRepositoryLaneRunning(repoLaneKey),
			GuardStateNeedingAttention = guardStates.TryGetValue(row.RepositoryFullName, out var guardState)
				? guardState
				: null
		});
	}

	private static void AddPackagesNode(List<NavItem> items, string organization, string repoKey, RepositoryDashboardRow row)
	{
		var packagesKey = PackagesKey(row.RepositoryFullName);

		// The branch takes the worst of its packages rather than a fixed grey. It was hardcoded to
		// text-muted, so it read as "something here is unknown" on every repository, including ones
		// where every package was known to be exactly at the tag.
		var packagesStatus = NavHealthRollup.Worst(
			row.Packages.Select(package => PackageTagStatus(package.MatchesTag(row.LatestTag))));

		items.Add(new NavItem
		{
			Key = packagesKey,
			Text = $"Packages ({row.Packages.Count})",
			ParentKey = repoKey,
			IconCss = NavHealthRollup.Icon("fas fa-box", packagesStatus),
			HealthStatus = packagesStatus,
			View = NavView.None,
			Organization = organization,
			RepositoryFullName = row.RepositoryFullName,
			IsLeaf = row.Packages.Count == 0,
			// Ahead of the categories, so the shape reads the same on every repository.
			SortOrder = 1
		});
	}

	private static void AddPackageNodes(List<NavItem> items, string organization, RepositoryDashboardRow row)
	{
		var packagesKey = PackagesKey(row.RepositoryFullName);

		foreach (var package in row.Packages.OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase))
		{
			var packageStatus = PackageTagStatus(package.MatchesTag(row.LatestTag));

			items.Add(new NavItem
			{
				Key = PackageKey(row.RepositoryFullName, package.PackageId),
				Text = package.LatestVersion is null
					? package.PackageId
					: $"{package.PackageId}  {package.LatestVersion}",
				ParentKey = packagesKey,
				IconCss = NavHealthRollup.Icon("fas fa-cube", packageStatus),
				HealthStatus = packageStatus,
				View = NavView.PackageDetail,
				Organization = organization,
				RepositoryFullName = row.RepositoryFullName,
				PackageId = package.PackageId,
				IsLeaf = true
			});
		}
	}

	private static void AddOpenIssueNodes(List<NavItem> items, string organization, string repoKey, RepositoryDashboardRow row)
	{
		// Open issues and pull requests. One branch for both kinds, because GitHub's own model
		// treats a pull request as an issue and because the reader wants the thing that has gone
		// unanswered longest, whichever kind it happens to be. The leaf glyph says which.
		var repoIssuesKey = RepoIssuesKey(row.RepositoryFullName);
		var nowUtc = DateTimeOffset.UtcNow;

		// An empty inbox means one of two things: genuinely nothing open, or nothing read. Only
		// OpenIssuesKnown tells them apart. Having asked the assessment instead was wrong twice
		// over — a locally-assessed repository never fetches an inbox at all unless a client was
		// to hand, and a fetch that failed and was swallowed also leaves an assessed row with an
		// empty list. Both drew a green "Issues (0)" over an inbox nobody had read.
		var issueStatus = row.OpenIssues.Count > 0
			? NavHealthRollup.Worst(row.OpenIssues.Select(issue => NavHealthRollup.FromSeverity(issue.SeverityAt(nowUtc))))
			: row.OpenIssuesKnown
				? PackageHealthStatus.Success
				: PackageHealthStatus.Unknown;

		items.Add(new NavItem
		{
			Key = repoIssuesKey,
			// The count is every open item, healthy ones included: it answers "what is in this
			// inbox". The repository's IssueCount, which counts only the unanswered, is a
			// different question and deliberately a different number.
			Text = $"Issues ({row.OpenIssues.Count})",
			ParentKey = repoKey,
			IconCss = NavHealthRollup.Icon("fas fa-comments", issueStatus),
			HealthStatus = issueStatus,
			// Its own view, not None: this is where a Dependabot backlog is read, so it has to be
			// where triage is started from. With None the node selected cleanly and rendered the
			// getting-started placeholder, so the action had nowhere to live.
			View = NavView.RepositoryIssuesDetail,
			Organization = organization,
			RepositoryFullName = row.RepositoryFullName,
			IsLeaf = row.OpenIssues.Count == 0,
			// Rank 2, between Packages (1) and the categories (3).
			SortOrder = 2
		});

		foreach (var issue in row.OpenIssues)
		{
			var severity = issue.SeverityAt(nowUtc);
			var severityRank = severity switch
			{
				AssessmentSeverity.Critical => 0,
				AssessmentSeverity.Error => 1,
				_ => 2
			};

			items.Add(new NavItem
			{
				Key = RepoIssueKey(row.RepositoryFullName, issue.Number),
				Text = $"#{issue.Number} {issue.Title}",
				ParentKey = repoIssuesKey,
				IconCss = NavHealthRollup.Icon(
					issue.IsPullRequest ? "fas fa-code-pull-request" : "fas fa-circle-dot",
					NavHealthRollup.FromSeverity(severity)),
				HealthStatus = NavHealthRollup.FromSeverity(severity),
				View = NavView.RepositoryIssueDetail,
				Organization = organization,
				RepositoryFullName = row.RepositoryFullName,
				IssueNumber = issue.Number,
				IsLeaf = true,
				// Worst first, then oldest first within a band. PDTree breaks SortOrder ties on
				// Text, and alphabetical order on "#1000" against "#99" is meaningless, so the
				// rank has to carry the number rather than leave it to the tie-break.
				SortOrder = (severityRank * 1_000_000) + Math.Min(issue.Number, 999_999)
			});
		}
	}

	private static void AddAssessmentNodes(List<NavItem> items, string organization, string repoKey, RepositoryDashboardRow row)
	{
		// Category sub-nodes (only if assessed). An assessment that threw before producing a result
		// leaves Assessment null with the reason recorded on StatusMessage — silently skipping past
		// it here is exactly how a repository could look unassessed forever instead of broken.
		if (row.Assessment is null)
		{
			AddAssessmentFailedNode(items, organization, repoKey, row);
			return;
		}

		foreach (var category in row.CategorySummaries.Keys.OrderBy(c => c.ToString()))
		{
			AddCategoryNodes(items, organization, repoKey, row, row.Assessment, category);
		}
	}

	private static void AddAssessmentFailedNode(List<NavItem> items, string organization, string repoKey, RepositoryDashboardRow row)
	{
		if (row.Status != PackageStatus.Error)
		{
			return;
		}

		items.Add(new NavItem
		{
			Key = AssessmentErrorKey(row.RepositoryFullName),
			Text = string.IsNullOrWhiteSpace(row.StatusMessage)
				? "Assessment failed"
				: $"Assessment failed: {row.StatusMessage}",
			ParentKey = repoKey,
			IconCss = "fas fa-triangle-exclamation text-danger",
			HealthStatus = PackageHealthStatus.Error,
			View = NavView.None,
			Organization = organization,
			RepositoryFullName = row.RepositoryFullName,
			IsLeaf = true,
			SortOrder = 3
		});
	}

	private static void AddCategoryNodes(
		List<NavItem> items,
		string organization,
		string repoKey,
		RepositoryDashboardRow row,
		RepoAssessment assessment,
		AssessmentCategory category)
	{
		var catKey = CategoryKey(row.RepositoryFullName, category);
		var catFailures = assessment.RuleResults
			.Where(r => !r.Passed && r.Category == category)
			.ToList();
		var catHasErrors = catFailures.Any(r => r.Severity is AssessmentSeverity.Critical or AssessmentSeverity.Error);
		var catHasWarnings = catFailures.Any(r => r.Severity == AssessmentSeverity.Warning);

		items.Add(new NavItem
		{
			Key = catKey,
			Text = category.ToString(),
			ParentKey = repoKey,
			IconCss = GetHealthIcon(true, catFailures.Count, catHasErrors, catHasWarnings),
			View = NavView.CategoryDetail,
			Organization = organization,
			RepositoryFullName = row.RepositoryFullName,
			Category = category,
			IsLeaf = catFailures.Count == 0,
			// Rank 3, after the repo-level Work node (0), Packages (1) and Issues (2) — see the
			// comment beside the Work node for why this must be explicit rather than tie-broken.
			SortOrder = 3,
			IssueCount = catFailures.Count,
			HasErrors = catHasErrors,
			HasWarnings = catHasWarnings
		});

		// Individual failing rule nodes under each category
		foreach (var rule in catFailures.OrderBy(r => r.RuleId))
		{
			items.Add(new NavItem
			{
				Key = RuleKey(row.RepositoryFullName, rule.RuleId),
				Text = $"{rule.RuleId} {rule.RuleName}",
				ParentKey = catKey,
				IconCss = GetRuleIcon(rule.Severity),
				View = NavView.RuleDetail,
				Organization = organization,
				RepositoryFullName = row.RepositoryFullName,
				Category = category,
				RuleId = rule.RuleId,
				IsLeaf = true,
				IssueCount = 1,
				HasErrors = rule.Severity is AssessmentSeverity.Critical or AssessmentSeverity.Error,
				HasWarnings = rule.Severity == AssessmentSeverity.Warning
			});
		}
	}

	/// <summary>
	/// Applies the package-name regex and locally-cloned filters.
	/// </summary>
	private List<RepositoryDashboardRow>? ApplyFilters(List<RepositoryDashboardRow>? rows)
	{
		if (rows is null)
		{
			return null;
		}

		var filtered = rows.AsEnumerable();

		if (FilterRegex is not null)
		{
			// The repository matches on its own name or on any package it publishes, so filtering for a
			// package still finds the repository that holds it.
			filtered = filtered.Where(r =>
				FilterRegex.IsMatch(r.RepositoryFullName)
				|| r.Packages.Any(p => FilterRegex.IsMatch(p.PackageId)));
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
	private static bool BelongsToOrganization(RepositoryDashboardRow row, string organization, string fallbackOrganization)
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
	/// <remarks>
	/// Assessment health and nothing else. Clone state, a dirty tree and drift from origin used to be
	/// folded in here as extra classes, which made one glyph answer three questions at once and left
	/// git state colouring what reads as a health indicator. They are badges on the right of the node
	/// now — see <c>NavItem.IsClonedLocally</c>, <c>IsWorkingTreeDirty</c>, <c>IsSyncedWithOrigin</c>
	/// and <c>BuildState</c> — so this answers only "how does this repository score against the
	/// rules".
	/// </remarks>
	public static string BuildRepositoryIconCss(RepositoryDashboardRow row)
		=> GetPackageHealthIcon(row.HealthStatus);

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
