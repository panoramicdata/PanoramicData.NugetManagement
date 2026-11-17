using System.Text.RegularExpressions;
using PanoramicData.Blazor;
using PanoramicData.Blazor.Models;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class NavTreeDataProvider
{
	/// <summary>
	/// Builds the tree node key for an organisation. Keys must be namespaced per organisation:
	/// PDTree throws on a duplicate key and swallows the exception, so a collision renders the whole
	/// tree empty with nothing logged to the browser console.
	/// </summary>
	public static string OrgKey(string organization) => $"org:{organization}";

	/// <summary>Builds the key for an organisation's "Repositories" container.</summary>
	public static string ReposKey(string organization) => $"repos:{organization}";

	/// <summary>
	/// Whether a node can be selected. Every node can except the "Loading repositories..." placeholder.
	/// Organisation, Repositories and Issues are all selectable: each shows the estate table.
	/// </summary>
	public static bool IsSelectable(NavItem? item)
		=> item is not null
			&& !item.Key.StartsWith("repos-loading:", StringComparison.Ordinal);

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

	/// <summary>Builds the key for the node reporting that a repository's assessment itself failed.</summary>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	public static string AssessmentErrorKey(string repositoryFullName) => $"assessmenterror:{repositoryFullName}";

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
	public static string OrganisationsKey { get; } = "organisations";

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
}
