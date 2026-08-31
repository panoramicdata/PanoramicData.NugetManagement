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
		RegressionGuardService? regressionGuard = null,
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

	/// <summary>Builds the key for an organisation's "Not governed" branch.</summary>
	public static string NotGovernedKey(string organization) => $"notgoverned:{organization}";

	/// <summary>Builds the key for a repository node.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public static string RepoKey(string repositoryFullName) => $"repo:{repositoryFullName}";

	/// <summary>Builds the key for a repository's "Packages" container.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public static string PackagesKey(string repositoryFullName) => $"pkgs:{repositoryFullName}";

	/// <summary>Builds the key for one package published by a repository.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="packageId">The NuGet package identifier.</param>
	public static string PackageKey(string repositoryFullName, string packageId)
		=> $"pkg:{repositoryFullName}:{packageId}";

	/// <summary>Builds the key for a repository's GitHub "Issues" container.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <remarks>
	/// Prefixed "repoissues" rather than "issues": <see cref="IssuesKey"/> already owns that prefix
	/// for the organisation's rule-failure branch, and PDTree throws on a duplicate key and swallows
	/// the exception, rendering the whole tree empty with nothing in the console.
	/// </remarks>
	public static string RepoIssuesKey(string repositoryFullName) => $"repoissues:{repositoryFullName}";

	/// <summary>Builds the key for one open issue or pull request of a repository.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="number">The issue or pull request number.</param>
	public static string RepoIssueKey(string repositoryFullName, int number)
		=> $"repoissue:{repositoryFullName}:{number}";

	/// <summary>Builds the key for one assessment category of a repository.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="category">The assessment category.</param>
	public static string CategoryKey(string repositoryFullName, AssessmentCategory category)
		=> $"cat:{repositoryFullName}:{category}";

	/// <summary>Builds the key for one failing rule of a repository.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="ruleId">The rule identifier.</param>
	public static string RuleKey(string repositoryFullName, string ruleId)
		=> $"rule:{repositoryFullName}:{ruleId}";

	/// <summary>Builds the key for a repository's "Work" container.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public static string WorkKey(string repositoryFullName) => $"work:{repositoryFullName}";

	/// <summary>Builds the key for an organisation's "Work" container.</summary>
	/// <param name="organization">The organisation.</param>
	public static string OrgWorkKey(string organization) => $"work-org:{organization}";

	/// <summary>Builds the key for one queued work item's node.</summary>
	/// <param name="workItemId">The item's identifier.</param>
	public static string WorkItemKey(string workItemId) => $"work-item:{workItemId}";

	/// <summary>
	/// Builds the key for a work item as it appears under an organisation's own Work node.
	/// </summary>
	/// <param name="workItemId">The work item.</param>
	/// <remarks>
	/// Distinct from <see cref="WorkItemKey"/> because the same item appears twice: once under the
	/// repository whose lane it is on, and once under the organisation rolling that lane up. Two tree
	/// nodes sharing a key is a bug in the tree itself, so the mirror gets its own namespace while
	/// keeping the item's identity in <see cref="NavItem.WorkItemId"/> — which is what stopping it uses.
	/// </remarks>
	public static string OrgWorkItemKey(string workItemId) => $"work-org-item:{workItemId}";

	/// <summary>
	/// The key of the always-expanded top-level container. Unlike the other container nodes it is
	/// selectable, because selecting it shows the estate overview.
	/// </summary>
	public const string OrganisationsKey = "organisations";

	/// <summary>
	/// Whether a node stands for a repository itself, rather than for something inside one.
	/// </summary>
	/// <remarks>
	/// Every node beneath a repository carries its <see cref="NavItem.RepositoryFullName"/>, because
	/// that is true of them and the selection handlers need it. Only this node, though, may offer the
	/// actions that act on the whole repository: excluding it from governance is not something one of
	/// its packages, categories or rules can do.
	/// </remarks>
	public static bool IsRepositoryNode(NavItem item)
		=> item.Key.StartsWith("repo:", StringComparison.Ordinal);

	/// <summary>
	/// The repository a node belongs to, taken from its key, or null for nodes above the repository
	/// layer. The owner/name identity contains no colon, so it is the segment after the prefix.
	/// </summary>
	/// <param name="key">The node key.</param>
	public static string? RepositoryFromKey(string key)
	{
		var prefixEnd = key.IndexOf(':', StringComparison.Ordinal);
		if (prefixEnd < 0)
		{
			return null;
		}

		var prefix = key[..prefixEnd];
		if (prefix is not ("repo" or "pkgs" or "pkg" or "cat" or "rule" or "repoissues" or "repoissue"))
		{
			return null;
		}

		var rest = key[(prefixEnd + 1)..];
		var next = rest.IndexOf(':', StringComparison.Ordinal);
		return next < 0 ? rest : rest[..next];
	}

	/// <summary>
	/// Whether a node is one of the tree's container headings — Organisations, Repositories, Issues,
	/// a repository's Packages and a repository's own Issues branch. They share a heading colour
	/// rather than a status colour, so the template marks them for the stylesheet. Keyed off the node
	/// key rather than the glyph: the issue categories under Issues use the same glyph as Issues
	/// itself, and telling them apart by the severity class on the icon fails whenever the container
	/// carries one too.
	/// </summary>
	public static bool IsContainerNode(NavItem item)
		=> item.Key == OrganisationsKey
			|| item.Key.StartsWith("pkgs:", StringComparison.Ordinal)
			|| item.Key.StartsWith("repos:", StringComparison.Ordinal)
			|| item.Key.StartsWith("issues:", StringComparison.Ordinal)
			|| item.Key.StartsWith("repoissues:", StringComparison.Ordinal);

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
		var isFiltering = FilterRegex is not null || LocalOnly;
		if (isFiltering && (visibleRows is null || visibleRows.Count == 0))
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

		var totalIssues = countedRows?.Sum(r => r.TotalFailures) ?? 0;
		var hasAnyErrors = countedRows?.Any(r => r.TotalCriticals > 0 || r.TotalErrors > 0) == true;
		var hasAnyWarnings = countedRows?.Any(r => r.TotalWarnings > 0) == true;

		var reposStatus = NavHealthRollup.ForRepositories(countedRows);

		// The Issues branch is built before the organisation node it hangs off, because that node is
		// coloured by the worst of the two branches and NavItem is immutable once created.
		var issuesItems = new List<NavItem>();
		var issuesStatus = AddIssueHierarchy(issuesItems, organization, issuesKey, orgKey, countedRows);

		var orgStatus = NavHealthRollup.Worst(reposStatus, issuesStatus);

		items.Add(new NavItem
		{
			Key = orgKey,
			Text = organization,
			ParentKey = OrganisationsKey,
			IconCss = NavHealthRollup.Icon("fas fa-people-group", orgStatus),
			View = NavView.Home,
			Organization = organization,
			IsLeaf = false,
			SortOrder = orgIndex,
			IssueCount = totalIssues,
			HasErrors = hasAnyErrors,
			HasWarnings = hasAnyWarnings,
			HealthStatus = orgStatus,
			IsBusy = IsLoading
		});

		items.Add(new NavItem
		{
			Key = reposKey,
			Text = "Repositories",
			ParentKey = orgKey,
			IconCss = NavHealthRollup.Icon("fas fa-cubes", reposStatus),
			View = NavView.Home,
			Organization = organization,
			IsLeaf = false,
			SortOrder = 0,
			HealthStatus = reposStatus
		});

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

		return orgStatus;
	}

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
			var repoKey = RepoKey(row.RepositoryFullName);
			var repoLaneKey = RepositoryLaneKey(row.RepositoryFullName);
			var repoIssues = row.TotalFailures;
			var repoHasErrors = row.TotalCriticals > 0 || row.TotalErrors > 0;
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
				SortOrder = 0
			});

			AddWorkNodes(
				items,
				WorkKey(row.RepositoryFullName),
				repoKey,
				repoLaneKey,
				organization,
				row.RepositoryFullName,
				// Rank 1, between Packages (0) and the categories (2). Explicit rather than
				// tie-broken: PDTree falls back to alphabetical order, and "Work" loses that race
				// against every category name.
				sortOrder: 1);

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
				SortOrder = 1
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

			// Category sub-nodes (only if assessed)
			if (row.Assessment is null)
			{
				continue;
			}

			foreach (var category in row.CategorySummaries.Keys.OrderBy(c => c.ToString()))
			{
				var catKey = CategoryKey(row.RepositoryFullName, category);
				var catFailures = row.Assessment.RuleResults
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
					// Rank 2, after Packages (0) and the repo-level Work node (1) — see the comment
					// beside that node for why this must be explicit rather than tie-broken.
					SortOrder = 2,
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
		}
	}

	/// <summary>
	/// Adds a lane's "Work" container and one node per outstanding item, or nothing when there is
	/// nothing to show under it. An empty container would be a node to open and find nothing in.
	/// </summary>
	/// <remarks>
	/// An organisation's node is shown when its own lane has items <em>or any repository beneath it
	/// does</em>, which is not the same rule as a repository's. It has to be: the node's header
	/// carries the "stop everything below here" button, which is what a fanned-out bulk action is
	/// stopped with now that it is many items rather than one. A discovery item runs on the
	/// organisation lane, fans out into forty repository lanes and then completes — so with the
	/// narrower rule the button would disappear at the exact moment forty lanes started needing it.
	/// <para>
	/// Its children are still only the organisation lane's own items. The descendants belong to their
	/// own repositories' work nodes, which is why the count says where the work actually is rather
	/// than implying it is all here.
	/// </para>
	/// </remarks>
	private void AddWorkNodes(
		List<NavItem> items,
		string workKey,
		string parentKey,
		string laneKey,
		string organization,
		string? repositoryFullName,
		int sortOrder)
	{
		var laneItems = _workLanes?.ItemsFor(laneKey) ?? [];

		// Only the organisation node rolls up. A repository is the bottom of the hierarchy: there is
		// nothing beneath it to count.
		var itemsBelow = repositoryFullName is null
			? CountWorkItemsUnderOrganisation(organization, laneKey)
			: 0;

		if (laneItems.Count == 0 && itemsBelow == 0)
		{
			return;
		}

		items.Add(new NavItem
		{
			Key = workKey,
			Text = WorkNodeText(laneItems.Count, itemsBelow),
			ParentKey = parentKey,
			IconCss = "fas fa-list-check",
			View = NavView.None,
			Organization = organization,
			RepositoryFullName = repositoryFullName,
			LaneKey = laneKey,
			// Expandable whenever it covers anything at all, its descendants' work included. It used to
			// be a leaf when its own lane was empty, so an organisation reading "Work (3 below)" could
			// not be opened to see those three — and the stop-all button on it reached work the tree
			// would not show.
			IsLeaf = laneItems.Count == 0 && itemsBelow == 0,
			SortOrder = sortOrder
		});

		for (var index = 0; index < laneItems.Count; index++)
		{
			var workItem = laneItems[index];

			items.Add(new NavItem
			{
				Key = WorkItemKey(workItem.Id),
				Text = workItem.Title,
				ParentKey = workKey,
				IconCss = workItem.State switch
				{
					Models.WorkItemState.Running => "fas fa-circle-notch fa-spin",
					Models.WorkItemState.Cancelling => "fas fa-rotate-left fa-spin",
					_ => "fas fa-clock"
				},
				View = NavView.None,
				Organization = organization,
				RepositoryFullName = repositoryFullName,
				LaneKey = laneKey,
				WorkItemId = workItem.Id,
				WorkItemState = workItem.State,
				WorkItemProgress = workItem.Progress,
				IsLeaf = true,
				// The lane's own order, not alphabetical: the queue's order is the information.
				SortOrder = index
			});
		}

		if (itemsBelow == 0)
		{
			return;
		}

		// The work in the repositories' own lanes, mirrored here. Listed rather than merely counted so
		// that the node reporting it can be opened to see it, and so the stop-all button on this node
		// never reaches anything invisible.
		var below = WorkItemsUnderOrganisation(organization, laneKey);

		for (var index = 0; index < below.Count; index++)
		{
			var (itemRepositoryFullName, workItem) = below[index];
			var shortName = ShortRepositoryName(itemRepositoryFullName);

			items.Add(new NavItem
			{
				Key = OrgWorkItemKey(workItem.Id),
				Text = MirroredWorkText(shortName, workItem.Title),
				ParentKey = workKey,
				IconCss = workItem.State switch
				{
					Models.WorkItemState.Running => "fas fa-circle-notch fa-spin",
					Models.WorkItemState.Cancelling => "fas fa-rotate-left fa-spin",
					_ => "fas fa-clock"
				},
				View = NavView.None,
				Organization = organization,
				RepositoryFullName = itemRepositoryFullName,
				LaneKey = RepositoryLaneKey(itemRepositoryFullName),
				WorkItemId = workItem.Id,
				WorkItemState = workItem.State,
				WorkItemProgress = workItem.Progress,
				IsLeaf = true,
				// After this node's own items, keeping "here" above "below" as the label describes them.
				SortOrder = laneItems.Count + index
			});
		}
	}

	/// <summary>
	/// The outstanding work in an organisation's repositories' lanes, with the repository each item
	/// belongs to.
	/// </summary>
	/// <param name="organization">The organisation whose descendants to gather.</param>
	/// <param name="ownLaneKey">The organisation's own lane, which is listed separately.</param>
	private List<(string RepositoryFullName, Models.WorkItem Item)> WorkItemsUnderOrganisation(
		string organization,
		string ownLaneKey)
		=> _workLanes is null
			? []
			: [.. _workLanes.Lanes
				.Where(lane => !string.Equals(lane.Key, ownLaneKey, StringComparison.Ordinal)
					&& string.Equals(lane.Organization, organization, StringComparison.OrdinalIgnoreCase))
				.OrderBy(lane => lane.Key, StringComparer.OrdinalIgnoreCase)
				.SelectMany(lane => lane.Items
					.Select(item => (item.RepositoryFullName ?? lane.Key, item)))];

	/// <summary>
	/// A mirrored item's label: the repository, then what the work is.
	/// </summary>
	/// <remarks>
	/// The same title appears under every repository — twelve "Re-assess" items are indistinguishable
	/// without it. The repository is left off where the title already names it, which most of them do,
	/// rather than reading "Athonet.Api — Re-assess Athonet.Api".
	/// </remarks>
	private static string MirroredWorkText(string repositoryShortName, string title)
		=> title.Contains(repositoryShortName, StringComparison.OrdinalIgnoreCase)
			? title
			: $"{repositoryShortName} — {title}";

	/// <summary>The repository's name without its owner.</summary>
	private static string ShortRepositoryName(string repositoryFullName)
		=> repositoryFullName.Contains('/', StringComparison.Ordinal)
			? repositoryFullName.Split('/')[^1]
			: repositoryFullName;

	/// <summary>
	/// One repository's lane key, as <see cref="WorkDescriptor.LaneKey"/> builds it.
	/// </summary>
	/// <param name="repositoryFullName">The repository, as <c>owner/name</c>.</param>
	public static string RepositoryLaneKey(string repositoryFullName)
		=> $"repo:{repositoryFullName.ToLowerInvariant()}";

	/// <summary>
	/// Whether a repository is actually working, as opposed to merely having work queued.
	/// </summary>
	/// <param name="laneKey">The repository's lane.</param>
	private bool IsRepositoryLaneRunning(string laneKey)
		=> (_workLanes?.ItemsFor(laneKey) ?? [])
			.Any(i => i.State is Models.WorkItemState.Running or Models.WorkItemState.Cancelling);

	/// <summary>
	/// How much outstanding work sits in the lanes belonging to an organisation, other than in its own
	/// lane — that is, in its repositories'.
	/// </summary>
	/// <param name="organization">The organisation whose descendants to count.</param>
	/// <param name="ownLaneKey">The organisation's own lane, which is counted separately.</param>
	private int CountWorkItemsUnderOrganisation(string organization, string ownLaneKey)
		=> _workLanes is null
			? 0
			: _workLanes.Lanes
				.Where(lane => !string.Equals(lane.Key, ownLaneKey, StringComparison.Ordinal)
					&& string.Equals(lane.Organization, organization, StringComparison.OrdinalIgnoreCase))
				.Sum(lane => lane.Items.Count);

	/// <summary>
	/// The work node's label, saying where the work it covers actually is.
	/// </summary>
	/// <remarks>
	/// A bare total would claim that forty repository items are in this lane, and expanding the node
	/// to find one discovery item under it would then read as a bug. "Below" is where the stop-all
	/// button reaches, which is the whole reason the node is shown at all.
	/// </remarks>
	/// <param name="ownCount">Items in this node's own lane.</param>
	/// <param name="itemsBelow">Items in lanes beneath it, which it does not list.</param>
	private static string WorkNodeText(int ownCount, int itemsBelow) => (ownCount, itemsBelow) switch
	{
		(0, var below) => $"Work ({below} below)",
		(var own, 0) => $"Work ({own})",
		var (own, below) => $"Work ({own} here, {below} below)"
	};

	/// <summary>
	/// Adds the Issues branch for one organisation: category → rule, the "dimensional flip" of the
	/// Repositories branch. It deliberately stops at rule level — a rule can affect every repository
	/// in the organisation, so listing repositories here would add thousands of sidebar nodes. The
	/// affected repositories and the bulk actions belong in the centre panel, which has room for
	/// per-repository checkboxes.
	/// </summary>
	/// <returns>
	/// The rolled-up health of the branch: the worst severity across its categories, so that the
	/// colour always has a visible cause beneath it. Grey only while nothing has been assessed at all,
	/// when the branch has no children either.
	/// </returns>
	private static PackageHealthStatus AddIssueHierarchy(
		List<NavItem> items,
		string organization,
		string issuesKey,
		string orgKey,
		List<RepositoryDashboardRow>? visibleRows)
	{
		// One entry per repository, which is what the rules were evaluated against. While a package
		// stood in for its repository, a repository publishing four of them counted as four affected
		// repositories against every rule it failed.
		// Augmented with the open Dependabot pull requests, exactly as DashboardService does when it
		// builds the same view for the centre panel: the tree's counts and the panel's contents have to
		// be the same answer, or one of them is lying.
		var assessed = visibleRows?
			.Where(r => r.Assessment is not null)
			.Select(r => new AssessedPackage(
				r.RepositoryFullName,
				DependabotIssueSynthesizer.Augment(r),
				r.RepositoryFullName))
			.ToList();

		var view = assessed is { Count: > 0 }
			? IssueCentricViewBuilder.Build(assessed)
			: null;

		var categories = view?.Categories ?? [];

		// Once anything has been assessed the branch lists the whole catalogue, not just today's
		// failures: a rule nothing fails is still a rule the estate is held to, and it shows green.
		// Nothing assessed means no catalogue at all — see AddCleanCatalogue.
		var showsCatalogue = assessed is { Count: > 0 };

		var issuesStatus = NavHealthRollup.ForIssues(visibleRows, categories.Select(c => c.Severity));

		items.Add(new NavItem
		{
			Key = issuesKey,
			Text = "Issues",
			ParentKey = orgKey,
			IconCss = NavHealthRollup.Icon("fas fa-layer-group", issuesStatus),
			View = NavView.Issues,
			Organization = organization,
			IsLeaf = categories.Count == 0 && !showsCatalogue,
			SortOrder = 1,
			IssueCount = categories.Sum(c => c.IssueClasses.Sum(i => i.AffectedRepositoryCount)),
			HealthStatus = issuesStatus
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

		if (showsCatalogue)
		{
			AddCleanCatalogue(items, organization, issuesKey, categories);
		}

		return issuesStatus;
	}

	/// <summary>
	/// Adds the rest of the rule catalogue to the Issues branch: every category that owns a rule, and
	/// every rule, that no failure already put there. These are the green ones — nothing fails them.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Only called once something has been assessed. Painting the catalogue green over an organisation
	/// nobody has assessed would claim a clean bill of health that was never checked — the same
	/// distinction the repository's own Issues node makes between an empty inbox and an unread one.
	/// </para>
	/// <para>
	/// Failing entries keep the positions they were given above, and these sort after them, so what
	/// needs attention stays at the top of each list. A rule is matched by id alone: a synthesised
	/// Dependabot result carries a rule id the registry never had, and belongs in the branch on its
	/// own terms rather than being paired with a catalogue entry.
	/// </para>
	/// </remarks>
	private static void AddCleanCatalogue(
		List<NavItem> items,
		string organization,
		string issuesKey,
		IReadOnlyList<IssueCategoryGroup> failing)
	{
		var failingRuleIds = failing
			.SelectMany(category => category.IssueClasses.Select(issueClass => issueClass.RuleId))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var categoryOrder = failing.Count;

		var catalogue = RuleRegistry.Rules
			.GroupBy(rule => rule.Category)
			.OrderBy(group => group.Key.ToString(), StringComparer.OrdinalIgnoreCase);

		foreach (var group in catalogue)
		{
			var categoryKey = $"icat:{organization}:{group.Key}";
			var alreadyListed = failing.FirstOrDefault(category => category.Category == group.Key);

			if (alreadyListed is null)
			{
				items.Add(new NavItem
				{
					Key = categoryKey,
					Text = group.Key.ToString(),
					ParentKey = issuesKey,
					IconCss = "fas fa-layer-group text-success",
					View = NavView.IssueCategoryDetail,
					Organization = organization,
					Category = group.Key,
					IsLeaf = false,
					SortOrder = categoryOrder++,
					IssueCount = 0,
					AffectedRepoCount = 0
				});
			}

			// Clean rules follow whatever failing ones the category already contributed.
			var ruleOrder = alreadyListed?.IssueClasses.Count ?? 0;

			foreach (var rule in group.OrderBy(rule => rule.RuleId, StringComparer.OrdinalIgnoreCase))
			{
				if (failingRuleIds.Contains(rule.RuleId))
				{
					continue;
				}

				items.Add(new NavItem
				{
					Key = $"irule:{organization}:{group.Key}:{rule.RuleId}",
					Text = $"{rule.RuleId} {rule.RuleName}",
					ParentKey = categoryKey,
					IconCss = "fas fa-check-circle text-success",
					View = NavView.IssueRuleDetail,
					Organization = organization,
					Category = group.Key,
					RuleId = rule.RuleId,
					IsLeaf = true,
					SortOrder = ruleOrder++,
					IssueCount = 0,
					AffectedRepoCount = 0
				});
			}
		}
	}

	/// <summary>
	/// Applies the package-name regex and locally-cloned filters.
	/// </summary>
	/// <summary>
	/// Adds the branch accounting for packages whose declared repository is not ours to govern.
	/// </summary>
	/// <remarks>
	/// Absent when there is nothing to report, so the tree gains a node only where there is something
	/// to do about it. The children are leaves and carry no view: there is nothing to act on, and the
	/// reason on each names the repository declared, which is the nuspec that needs correcting.
	/// </remarks>
	private static void AddNotGovernedNodes(
		List<NavItem> items,
		string organization,
		string orgKey,
		IReadOnlyList<UngovernedPackage> packages)
	{
		if (packages.Count == 0)
		{
			return;
		}

		var notGovernedKey = NotGovernedKey(organization);

		items.Add(new NavItem
		{
			Key = notGovernedKey,
			Text = $"Not governed ({packages.Count})",
			ParentKey = orgKey,
			IconCss = "fas fa-circle-question",
			View = NavView.None,
			Organization = organization,
			IsLeaf = false,
			// Rank 3, after Repositories (0), Issues (1) and the org-level Work node (2).
			SortOrder = 3
		});

		foreach (var package in packages.OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase))
		{
			items.Add(new NavItem
			{
				Key = $"notgoverned:{organization}:{package.PackageId}",
				Text = $"{package.PackageId} — {package.Reason}",
				ParentKey = notGovernedKey,
				IconCss = "fas fa-circle-question",
				View = NavView.None,
				Organization = organization,
				PackageId = package.PackageId,
				IsLeaf = true
			});
		}
	}

	/// <summary>
	/// The packages of one organisation that belong to no repository we govern, honouring the name
	/// filter. They have no repository, so they are held beside the rows rather than among them.
	/// </summary>
	private List<UngovernedPackage> UngovernedPackagesFor(string organization, string fallbackOrganization)
	{
		var packages = _cache.GetUngovernedPackages()
			.Where(package => string.IsNullOrEmpty(package.Organization)
				? string.Equals(organization, fallbackOrganization, StringComparison.OrdinalIgnoreCase)
				: string.Equals(package.Organization, organization, StringComparison.OrdinalIgnoreCase));

		if (FilterRegex is not null)
		{
			packages = packages.Where(package => FilterRegex.IsMatch(package.PackageId));
		}

		return [.. packages];
	}

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
	public static string BuildRepositoryIconCss(RepositoryDashboardRow row)
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
