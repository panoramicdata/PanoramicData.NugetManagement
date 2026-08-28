using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Decides which repositories a bulk apply will touch.
/// </summary>
/// <remarks>
/// One function, used for the number on the button, the list in the confirm dialog, the queue entry
/// and the rows handed to the service — because when the count and the run were decided separately
/// they disagreed. A category run reported "9 repos" and then visited 83: the caller counted
/// repositories with an auto-remediable instance, and the service re-derived its own list from any
/// failing rule in the category. The 74 extra were each synced and fully re-assessed before reporting
/// that there was nothing to do.
/// </remarks>
public static class BulkApplyScope
{
	/// <summary>
	/// The repositories a single-rule apply will touch: those with an auto-remediable instance of the
	/// rule that are also in scope.
	/// </summary>
	/// <param name="rule">The issue class being applied.</param>
	/// <param name="inScope">The repositories the user has in scope.</param>
	public static IReadOnlyList<string> ForRule(IssueClassGroup rule, ISet<string> inScope)
		=> Distinct(rule.Instances
			.Where(instance => instance.IsAutoRemediable && inScope.Contains(instance.RepositoryFullName))
			.Select(instance => instance.RepositoryFullName));

	/// <summary>
	/// The repositories a category apply will touch, deduplicated across the category's rules — one
	/// repository failing three rules in the category is visited once.
	/// </summary>
	/// <param name="category">The category being applied.</param>
	/// <param name="inScope">The repositories the user has in scope.</param>
	public static IReadOnlyList<string> ForCategory(IssueCategoryGroup category, ISet<string> inScope)
		=> Distinct(category.IssueClasses.SelectMany(rule => ForRule(rule, inScope)));

	/// <summary>
	/// The repositories a "fix everything" apply will touch, across every category.
	/// </summary>
	/// <param name="view">The issue-centric view being acted on.</param>
	/// <param name="inScope">The repositories the user has in scope.</param>
	public static IReadOnlyList<string> ForEverything(IssueCentricView view, ISet<string> inScope)
		=> Distinct(view.Categories.SelectMany(category => ForCategory(category, inScope)));

	private static IReadOnlyList<string> Distinct(IEnumerable<string> repositories)
		=> [.. repositories.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
}
