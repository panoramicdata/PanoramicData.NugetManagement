using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Decomposes work that spans repositories into one item per repository, each in its own lane.
/// </summary>
/// <remarks>
/// A bulk action is no longer a single queued item. It cannot be: a single item runs on a single
/// lane, and the whole point of lanes is that twelve repositories are twelve independent pieces of
/// work. What the user loses is one thing to stop; what they gain is twelve running at once, and one
/// repository's failure no longer ending the run for the other eleven. Stopping the lot is the
/// organisation node's "stop all", which is <see cref="WorkLaneService.CancelUnder"/>.
/// </remarks>
public sealed class WorkFanOut(WorkLaneService lanes)
{
	/// <summary>Queues a re-assessment of every given repository. Returns how many were queued.</summary>
	/// <param name="organization">The organisation they belong to.</param>
	/// <param name="rows">The repositories to re-assess.</param>
	/// <param name="consoleNodeKey">The console the output belongs to.</param>
	public int EnqueueReassess(
		string? organization,
		IReadOnlyList<RepositoryDashboardRow> rows,
		string? consoleNodeKey)
		=> rows.Count(row => lanes.Enqueue(
			$"Re-assess {row.RepositoryName}",
			WorkDescriptor.ForRepository(WorkKind.Reassess, organization, row.RepositoryFullName),
			$"reassess:{row.RepositoryFullName}",
			WorkflowStep.Reassess,
			consoleNodeKey) is not null);

	/// <summary>Queues a clone of every given candidate. Returns how many were queued.</summary>
	/// <param name="organization">The organisation they belong to.</param>
	/// <param name="targets">The repositories to clone.</param>
	/// <param name="consoleNodeKey">The console the output belongs to.</param>
	public int EnqueueClone(
		string organization,
		IReadOnlyList<RepositoryCloneCandidate> targets,
		string? consoleNodeKey)
		=> targets.Count(target => lanes.Enqueue(
			$"Clone {target.FullName}",
			WorkDescriptor.ForRepository(WorkKind.Clone, organization, target.FullName),
			$"clone:{target.FullName}",
			step: null,
			consoleNodeKey) is not null);

	/// <summary>
	/// Queues one rule's auto-fix against every affected repository, optionally following each fix
	/// with a commit and push in the same lane. Returns how many repositories were queued.
	/// </summary>
	/// <param name="organization">The organisation the repositories belong to.</param>
	/// <param name="ruleId">The rule to apply.</param>
	/// <param name="repositoryFullNames">The repositories it affects.</param>
	/// <param name="push">Whether to commit and push each repository after fixing it.</param>
	/// <param name="consoleNodeKey">The console the output belongs to.</param>
	public int EnqueueRule(
		string organization,
		string ruleId,
		IReadOnlyList<string> repositoryFullNames,
		bool push,
		string? consoleNodeKey)
	{
		var queued = 0;

		foreach (var repositoryFullName in repositoryFullNames)
		{
			var fix = lanes.Enqueue(
				$"Fix {ruleId} — {repositoryFullName}",
				WorkDescriptor.ForRepository(WorkKind.FixRule, organization, repositoryFullName, ("ruleId", ruleId)),
				$"fix-rule:{repositoryFullName}:{ruleId}",
				step: null,
				consoleNodeKey);

			if (fix is null)
			{
				continue;
			}

			queued++;

			// Queued behind the fix in the same lane, which is what makes "apply and push" atomic per
			// repository without any coordination: the lane is the ordering.
			if (push)
			{
				lanes.Enqueue(
					$"Commit & push {repositoryFullName}",
					WorkDescriptor.ForRepository(WorkKind.CommitAndPush, organization, repositoryFullName),
					$"commit-push:{repositoryFullName}:{ruleId}",
					WorkflowStep.CommitAndPush,
					consoleNodeKey);
			}
		}

		return queued;
	}
}
