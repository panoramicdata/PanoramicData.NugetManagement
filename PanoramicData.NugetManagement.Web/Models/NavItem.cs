using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Represents a navigation node in the PDTree sidebar.
/// </summary>
public class NavItem
{
	/// <summary>
	/// Unique key for this node.
	/// </summary>
	public required string Key { get; init; }

	/// <summary>
	/// Display text shown in the tree.
	/// </summary>
	public required string Text { get; init; }

	/// <summary>
	/// Parent key for tree hierarchy. Null for root-level nodes.
	/// </summary>
	public string? ParentKey { get; init; }

	/// <summary>
	/// Font Awesome icon CSS class (e.g. "fas fa-box").
	/// </summary>
	public string IconCss { get; init; } = "fas fa-circle";

	/// <summary>
	/// The type of view to show when this node is selected.
	/// </summary>
	public NavView View { get; init; } = NavView.None;

	/// <summary>
	/// The organisation this node belongs to. Every node below the "Organisations" container carries
	/// it, so a handler can tell which organisation a selection relates to without parsing the key.
	/// Empty for the top-level container itself.
	/// </summary>
	public string Organization { get; init; } = string.Empty;

	/// <summary>
	/// Whether this repository has been excluded from governance.
	/// </summary>
	public bool IsExcluded { get; init; }

	/// <summary>
	/// The repository this node represents, for repository-level nodes.
	/// </summary>
	public string? RepositoryFullName { get; init; }

	/// <summary>
	/// The build-guard state for this repository, when it is one the user should look at:
	/// a reverted regression, a guard error, or a build that was already failing.
	/// </summary>
	/// <remarks>
	/// Deliberately null for Verified, Queued and Building. Verified is the expected outcome, and a
	/// hundred green ticks are exactly what hides the two rows that need someone.
	/// </remarks>
	public GuardState? GuardStateNeedingAttention { get; init; }

	/// <summary>
	/// Optional associated package ID for package-level nodes.
	/// </summary>
	public string? PackageId { get; init; }

	/// <summary>
	/// For issue-hierarchy nodes, the number of distinct repositories affected. Distinct from
	/// <see cref="IssueCount"/>, which counts failures rather than repositories.
	/// </summary>
	public int AffectedRepoCount { get; init; }

	/// <summary>
	/// Optional associated assessment category for category-level nodes.
	/// </summary>
	public AssessmentCategory? Category { get; init; }

	/// <summary>
	/// Optional rule ID for rule-level leaf nodes.
	/// </summary>
	public string? RuleId { get; init; }

	/// <summary>
	/// For an open issue or pull request leaf, its GitHub number. Lets a selection be resolved back
	/// to its <see cref="PanoramicData.NugetManagement.Models.RepositoryIssue"/> without parsing the
	/// key.
	/// </summary>
	public int? IssueNumber { get; init; }

	/// <summary>
	/// Whether this node is a leaf (no children).
	/// </summary>
	public bool IsLeaf { get; init; }

	/// <summary>
	/// Explicit ordering rank among siblings; lower sorts first, ties broken by <see cref="Text"/>.
	/// PDTree sorts children alphabetically by text unless given a Sort comparison, which would put
	/// Critical below Info, so severity-ordered branches set this rather than relying on their label.
	/// </summary>
	public int SortOrder { get; init; }

	/// <summary>
	/// The number of issues at or below this node.  
	/// Used for displaying issue counts in the tree.
	/// </summary>
	public int IssueCount { get; init; }

	/// <summary>
	/// The rolled-up health of this subtree: the worst status of everything beneath it, with Unknown
	/// counting as the worst of all. Set on the branch nodes (Organisations, each organisation, its
	/// Repositories and its Issues), which colour their glyph from it. Null where the node's icon is
	/// resolved some other way, such as a package node reading live row state.
	/// </summary>
	public PackageHealthStatus? HealthStatus { get; init; }

	/// <summary>
	/// Whether this subtree has any errors (not just warnings).
	/// </summary>
	public bool HasErrors { get; init; }

	/// <summary>
	/// Whether this subtree has any warnings (not just info).
	/// </summary>
	public bool HasWarnings { get; init; }

	/// <summary>
	/// Whether the local working tree is dirty (has uncommitted changes).
	/// Only meaningful for package-level nodes where the repo is cloned locally.
	/// </summary>
	public bool IsWorkingTreeDirty { get; init; }

	/// <summary>
	/// Whether the repository is cloned locally, for the git badge on a repository node.
	/// </summary>
	public bool IsClonedLocally { get; init; }

	/// <summary>
	/// Whether the local branch is in step with origin, or null when that has never been established.
	/// </summary>
	public bool? IsSyncedWithOrigin { get; init; }

	/// <summary>
	/// What the last local build of this repository did, or null when that is not known.
	/// </summary>
	/// <remarks>
	/// Rendered as a badge of its own on the right of the node, never folded into
	/// <see cref="HealthStatus"/> or <see cref="IconCss"/>: failing to build is not failing a rule,
	/// and a red glyph that could mean either is a glyph that means neither.
	/// </remarks>
	public RepositoryBuildState? BuildState { get; init; }

	/// <summary>
	/// Whether this node should show a busy/loading spinner (e.g. the org node while
	/// the repository list is still being discovered/assessed).
	/// </summary>
	public bool IsBusy { get; init; }

	/// <summary>
	/// The queued work item this node represents, or null for every other kind of node.
	/// </summary>
	public string? WorkItemId { get; init; }

	/// <summary>Where the work item has got to, for work-item nodes.</summary>
	public WorkItemState? WorkItemState { get; init; }

	/// <summary>The work item's progress line, e.g. "repo 8 of 47". Null until it reports some.</summary>
	public string? WorkItemProgress { get; init; }

	/// <summary>
	/// The lane a work node covers, so its "stop everything" button knows what to clear.
	/// </summary>
	public string? LaneKey { get; init; }
}

/// <summary>
/// Identifies which view to render for a given navigation node.
/// </summary>
public enum NavView
{
	/// <summary>No view — branch node only.</summary>
	None,

	/// <summary>
	/// The landing page: what you can do here. Named Home rather than Dashboard because it shows
	/// guidance, not figures — the aggregate view lives on <see cref="Dashboard"/>.
	/// </summary>
	Home,

	/// <summary>
	/// The estate overview: progress toward every locally-cloned repository being clean, per-
	/// organisation counts, and the rules affecting the most repositories.
	/// </summary>
	Dashboard,

	/// <summary>
	/// Repository-level detail: the assessment, the clone, and every action that acts on the
	/// repository. What <see cref="PackageDetail"/> showed while a package stood in for its repository.
	/// </summary>
	RepositoryDetail,

	/// <summary>Package-level detail: the published version, its tag match and its listing state.</summary>
	PackageDetail,

	/// <summary>Category-level view within a package.</summary>
	CategoryDetail,

	/// <summary>Individual rule detail view.</summary>
	RuleDetail,

	/// <summary>Per-organisation settings (the cog).</summary>
	Settings,

	/// <summary>Application-wide settings (populated later).</summary>
	AppSettings,

	/// <summary>
	/// The issue-centric view for an organisation: the same failures grouped by issue rather than by
	/// repository. Org-scoped, which is why it hangs off the organisation node.
	/// </summary>
	Issues,

	/// <summary>
	/// One category of the issue hierarchy — every failing rule in that category across the
	/// organisation, with the affected repositories and bulk actions shown in the centre panel.
	/// </summary>
	IssueCategoryDetail,

	/// <summary>
	/// One rule of the issue hierarchy — the repositories it affects, with the per-rule bulk
	/// actions (apply auto-fixes, apply and push, copy combined AI prompt).
	/// </summary>
	IssueRuleDetail,

	/// <summary>
	/// One open GitHub issue or pull request of a repository: who raised it, when a maintainer last
	/// replied, and how stale that makes it.
	/// </summary>
	RepositoryIssueDetail,

	/// <summary>
	/// One repository's whole inbox: every open issue and pull request, what the last Dependabot
	/// triage pass concluded about each, and the action that starts another.
	/// </summary>
	RepositoryIssuesDetail,

	/// <summary>
	/// The whole estate: every repository in the organisation with its health, its git state and
	/// whether it builds, and a toolbar whose every step acts on all of them at once.
	/// </summary>
	/// <remarks>
	/// A view of its own rather than <see cref="Home"/>, which the organisation node and the landing
	/// page both already use — with the branch sharing it, nothing could tell "the estate" apart from
	/// "an organisation" to scope a bulk action by.
	/// </remarks>
	Repositories
}
