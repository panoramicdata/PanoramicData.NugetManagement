using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// One thing the local model could be asked to do: a rule, and the one file to do it in.
/// </summary>
/// <param name="RuleId">The rule to satisfy.</param>
/// <param name="Path">
/// The single file this session may change, or null when the fix is one piece of work spanning
/// whatever it needs to.
/// </param>
public sealed record AiFixTarget(string RuleId, string? Path)
{
	/// <summary>How the target reads in the console and in a queued item's title.</summary>
	public override string ToString() => Path is null ? RuleId : $"{RuleId} ({Path})";
}

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
	/// The sessions an AI fix could be queued for, in a stable order.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="remediations">The deterministic remediations, whose coverage is excluded.</param>
	/// <remarks>
	/// A local clone is required, and its absence produces no candidates rather than an error: the model
	/// works by editing files on disk, so a repository with no working tree has nothing for it to do.
	/// Offering the action anyway would queue an item that could only fail.
	/// <para>
	/// A failure whose advisory names <see cref="RuleAdvisory.Targets"/> becomes one candidate per file
	/// rather than one for the rule. The turn budget is per session, so three files in one session is a
	/// third of a budget each — and a small model spends the first third planning all three.
	/// </para>
	/// </remarks>
	public static IReadOnlyList<AiFixTarget> For(RepositoryDashboardRow row, RemediationRegistry remediations)
	{
		if (row.Assessment is null || !row.IsClonedLocally || row.LocalPath is null)
		{
			return [];
		}

		return
		[
			.. row.Assessment.RuleResults
				.Where(result => !result.Passed)
				.Where(result => remediations.Get(result.RuleId) is null)
				.SelectMany(Expand)
				.Distinct()
				.OrderBy(target => target.RuleId, StringComparer.OrdinalIgnoreCase)
				.ThenBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
		];
	}

	/// <summary>
	/// One failure, as the session or sessions that would address it.
	/// </summary>
	private static IEnumerable<AiFixTarget> Expand(RuleResult result)
	{
		var targets = result.Advisory?.Targets;

		return targets is { Count: > 0 }
			? targets
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(path => new AiFixTarget(result.RuleId, path))
			: [new AiFixTarget(result.RuleId, null)];
	}
}
