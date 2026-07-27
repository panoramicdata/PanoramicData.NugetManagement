using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Which level of the issue-centric hierarchy a node represents.
/// </summary>
public enum IssueNodeKind
{
	/// <summary>An assessment category, e.g. NuGetHygiene.</summary>
	Category,

	/// <summary>A single rule within a category, aggregated across every repository it affects.</summary>
	Rule,

	/// <summary>One repository affected by a rule.</summary>
	Repository
}

/// <summary>
/// A node in the issue-centric tree: Category → Rule → Repository, the dimensional flip of the
/// repo-centric navigation tree. Carries the group it was built from so the node template can
/// render severities, counts and remediation actions without looking anything up again.
/// </summary>
public class IssueNavItem
{
	/// <summary>
	/// Unique key for this node.
	/// </summary>
	public required string Key { get; init; }

	/// <summary>
	/// Parent key for tree hierarchy. Null for the top-level category nodes.
	/// </summary>
	public string? ParentKey { get; init; }

	/// <summary>
	/// Display text. The node template renders richer content, so this is mainly for tooltips and
	/// for PDTree's own text handling.
	/// </summary>
	public required string Text { get; init; }

	/// <summary>
	/// Which level of the hierarchy this node represents.
	/// </summary>
	public required IssueNodeKind Kind { get; init; }

	/// <summary>
	/// Whether this node has no children. Only repository nodes are leaves.
	/// </summary>
	public bool IsLeaf { get; init; }

	/// <summary>
	/// The category group, when <see cref="Kind"/> is <see cref="IssueNodeKind.Category"/>.
	/// </summary>
	public IssueCategoryGroup? Category { get; init; }

	/// <summary>
	/// The rule group, when <see cref="Kind"/> is <see cref="IssueNodeKind.Rule"/>.
	/// </summary>
	public IssueClassGroup? Rule { get; init; }

	/// <summary>
	/// The affected repository, when <see cref="Kind"/> is <see cref="IssueNodeKind.Repository"/>.
	/// </summary>
	public IssueInstance? Instance { get; init; }

	/// <summary>
	/// The rule this node belongs to. Set for both rule and repository nodes, so a repository node
	/// knows which rule failed on it.
	/// </summary>
	public string? RuleId { get; init; }
}
