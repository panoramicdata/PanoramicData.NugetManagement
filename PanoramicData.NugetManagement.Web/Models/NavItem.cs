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
	/// Optional associated package ID for package-level nodes.
	/// </summary>
	public string? PackageId { get; init; }

	/// <summary>
	/// Optional associated assessment category for category-level nodes.
	/// </summary>
	public AssessmentCategory? Category { get; init; }

	/// <summary>
	/// Optional rule ID for rule-level leaf nodes.
	/// </summary>
	public string? RuleId { get; init; }

	/// <summary>
	/// Whether this node is a leaf (no children).
	/// </summary>
	public bool IsLeaf { get; init; }

	/// <summary>
	/// The number of issues at or below this node.  
	/// Used for displaying issue counts in the tree.
	/// </summary>
	public int IssueCount { get; init; }

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
}

/// <summary>
/// Identifies which view to render for a given navigation node.
/// </summary>
public enum NavView
{
	/// <summary>No view — branch node only.</summary>
	None,

	/// <summary>Organisation-level dashboard overview.</summary>
	Dashboard,

	/// <summary>Package-level detail / assessment view.</summary>
	PackageDetail,

	/// <summary>Category-level view within a package.</summary>
	CategoryDetail,

	/// <summary>Individual rule detail view.</summary>
	RuleDetail,

	/// <summary>Application settings.</summary>
	Settings
}
