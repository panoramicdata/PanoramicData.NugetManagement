using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What a local model has to do in a repository: the failing rules no deterministic remediation covers,
/// and the human-raised issues nobody has analysed yet.
/// </summary>
/// <remarks>
/// Lifted out of the page. It used to read the one selected repository, which is why Fix with AI could
/// not act on many; as pure functions of a row it can be asked about every ticked repository, and
/// tested.
/// </remarks>
public static class AiFixTargets
{
	/// <summary>
	/// The human-raised issues in a repository that no analysis has judged yet.
	/// </summary>
	/// <remarks>
	/// A verdict is re-taken when the conversation has moved on since it was reached: a comment added
	/// after the fact may be the reporter answering the very question that made the model escalate,
	/// and a stale verdict shown as current is worse than none.
	/// <para>
	/// Only human-raised issues. Dependabot's belong to triage, which runs on Fix and does not need a
	/// model.
	/// </para>
	/// </remarks>
	/// <param name="row">The repository.</param>
	public static IReadOnlyList<RepositoryIssue> UnanalysedHumanIssues(RepositoryDashboardRow row)
		=>
		[
			.. row.OpenIssues
				.Where(HumanIssueFilter.IsHumanRaised)
				.Where(issue => issue.Analysis is null || IsVerdictStale(issue))
		];

	/// <summary>Whether the conversation has moved on since the verdict was reached.</summary>
	/// <param name="issue">The issue.</param>
	public static bool IsVerdictStale(RepositoryIssue issue)
		=> issue.AnalysedAtIssueUpdatedUtc is { } analysed
			&& issue.LastMaintainerReplyUtc is { } replied
			&& replied > analysed;

	/// <summary>
	/// How much a local model has to do in a repository.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="remediations">The deterministic remediations, whose coverage is excluded.</param>
	public static int WorkCount(RepositoryDashboardRow row, RemediationRegistry remediations)
		=> AiFixCandidates.For(row, remediations).Count + UnanalysedHumanIssues(row).Count;
}
