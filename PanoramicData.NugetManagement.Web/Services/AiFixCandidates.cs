using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Which of a repository's failing rules belong to Fix with AI.
/// </summary>
/// <remarks>
/// The complement of what <see cref="RemediationRegistry"/> covers: Fix does what a remediation can do,
/// Fix with AI does what nothing else can, and nothing is offered by both. That rule is enforced at
/// design time by a test forbidding a playbook for a remediable rule, and at run time here.
/// </remarks>
public static class AiFixCandidates
{
	/// <summary>
	/// The rule ids an AI fix could be queued for, in a stable order.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="remediations">The deterministic remediations, whose coverage is excluded.</param>
	/// <remarks>
	/// A local clone is required, and its absence produces no candidates rather than an error: the model
	/// works by editing files on disk, so a repository with no working tree has nothing for it to do.
	/// Offering the action anyway would queue an item that could only fail.
	/// </remarks>
	public static IReadOnlyList<string> For(RepositoryDashboardRow row, RemediationRegistry remediations)
	{
		if (row.Assessment is null || !row.IsClonedLocally || row.LocalPath is null)
		{
			return [];
		}

		return
		[
			.. row.Assessment.RuleResults
				.Where(result => !result.Passed)
				.Select(result => result.RuleId)
				.Where(ruleId => remediations.Get(ruleId) is null)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Order(StringComparer.OrdinalIgnoreCase)
		];
	}
}
