using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What pressing Fix should do, for one selection.
/// </summary>
/// <param name="ApplyRemediations">Whether to apply the auto-remediations of the failing rules.</param>
/// <param name="TriageDependabot">Whether to triage the repository's Dependabot pull requests.</param>
public sealed record FixActions(bool ApplyRemediations, bool TriageDependabot)
{
	/// <summary>Whether Fix would do anything at all, and so whether it should be offered.</summary>
	public bool HasAnything => ApplyRemediations || TriageDependabot;
}

/// <summary>
/// Maps what is selected to what Fix does about it.
/// </summary>
/// <remarks>
/// Fix is the only button that fixes things, and it fixes everything under the selected node. That
/// rule is the whole design: a second button for each kind of fixing is how a toolbar becomes
/// unreadable, and how a user ends up hunting for the one control that applies.
/// <para>
/// A separate type rather than a method on the page, because the page cannot be unit tested — the web
/// project has no bUnit reference — and this mapping is the part worth being sure of. Adding a
/// <see cref="NavView"/> without deciding what Fix means for it leaves it doing nothing, which is the
/// safe default.
/// </para>
/// </remarks>
public static class FixScope
{
	private static readonly FixActions _nothing = new(false, false);

	/// <summary>
	/// What Fix does for the given selection.
	/// </summary>
	/// <param name="view">The selected node's view.</param>
	public static FixActions For(NavView view) => view switch
	{
		// A repository, and the package inside it, contain both the failing rules and the inbox.
		NavView.RepositoryDetail or NavView.PackageDetail => new(true, true),

		// The inbox and one item in it contain pull requests and nothing else. No failing rule sits
		// beneath a pull request, so rewriting files here would be doing more than was asked.
		NavView.RepositoryIssuesDetail or NavView.RepositoryIssueDetail => new(false, true),

		// A category or a rule is scoped to rules. Closing pull requests is not part of that scope,
		// even though the repository's inbox is technically elsewhere in the same tree.
		NavView.CategoryDetail or NavView.RuleDetail => new(true, false),

		_ => _nothing
	};

	/// <summary>
	/// What pressing Fix will do, in a sentence, for showing next to the button.
	/// </summary>
	/// <param name="actions">What Fix does for this selection.</param>
	/// <param name="remediableRuleCount">How many failing rules have an auto-remediation.</param>
	/// <param name="dependabotPullRequestCount">How many open pull requests Dependabot raised.</param>
	/// <remarks>
	/// Fix rewrites files and closes other people's pull requests, so what it is about to do has to be
	/// legible before it is pressed rather than discoverable afterwards in the console. A half with
	/// nothing to do is left out entirely: "and triage 0 pull requests" reads as though something might
	/// still happen to them.
	/// </remarks>
	public static string Describe(
		FixActions actions,
		int remediableRuleCount,
		int dependabotPullRequestCount)
	{
		var clauses = new List<string>();

		if (actions.ApplyRemediations && remediableRuleCount > 0)
		{
			clauses.Add($"apply {remediableRuleCount} auto-{Plural(remediableRuleCount, "fix", "fixes")}");
		}

		if (actions.TriageDependabot && dependabotPullRequestCount > 0)
		{
			clauses.Add(
				$"triage {dependabotPullRequestCount} Dependabot pull "
				+ Plural(dependabotPullRequestCount, "request", "requests"));
		}

		return clauses.Count == 0
			? "Fix has nothing to do here."
			: $"Fix will {string.Join(" and ", clauses)}.";
	}

	private static string Plural(int count, string one, string many) => count == 1 ? one : many;
}
