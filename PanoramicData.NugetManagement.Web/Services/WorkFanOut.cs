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
	{
		var queued = 0;

		foreach (var row in rows)
		{
			var item = lanes.Enqueue(
				$"Re-assess {row.RepositoryName}",
				WorkDescriptor.ForRepository(WorkKind.Reassess, organization, row.RepositoryFullName),
				$"reassess:{row.RepositoryFullName}",
				WorkflowStep.Reassess,
				consoleNodeKey);

			if (item is not null)
			{
				queued++;
			}
		}

		return queued;
	}

	/// <summary>
	/// Queues Dependabot triage for every given repository. Returns how many were queued.
	/// </summary>
	/// <param name="organization">The organisation they belong to.</param>
	/// <param name="rows">The repositories to triage.</param>
	/// <param name="consoleNodeKey">The console the output belongs to.</param>
	/// <remarks>
	/// Queued separately from, and after, the re-assessment. Lanes run in order and a repository has
	/// one lane, so a triage queued after that repository's re-assessment is guaranteed to see the
	/// assessment it depends on — without any explicit dependency mechanism.
	/// </remarks>
	public int EnqueueTriageDependabot(
		string? organization,
		IReadOnlyList<RepositoryDashboardRow> rows,
		string? consoleNodeKey)
	{
		var queued = 0;

		foreach (var row in rows)
		{
			var item = lanes.Enqueue(
				$"Triage Dependabot for {row.RepositoryName}",
				WorkDescriptor.ForRepository(WorkKind.TriageDependabot, organization, row.RepositoryFullName),
				$"triagedependabot:{row.RepositoryFullName}",
				null,
				consoleNodeKey);

			if (item is not null)
			{
				queued++;
			}
		}

		return queued;
	}

	/// <summary>
	/// Queues one AI fix per rule for one repository. Returns how many were queued.
	/// </summary>
	/// <param name="organization">The organisation it belongs to.</param>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="ruleIds">The rules to fix, one item each.</param>
	/// <param name="consoleNodeKey">The console the output belongs to.</param>
	/// <remarks>
	/// One item per rule rather than one per repository: the prompt stays small, which is most of what
	/// makes a small model succeed, and a rule it cannot manage does not take the others with it. They
	/// share the repository's lane, so they run one at a time against that working tree regardless of how
	/// many are queued.
	/// </remarks>
	public int EnqueueAiFix(
		string? organization,
		string repositoryFullName,
		IReadOnlyList<string> ruleIds,
		string? consoleNodeKey)
	{
		var queued = 0;

		foreach (var ruleId in ruleIds)
		{
			var item = lanes.Enqueue(
				$"Fix {ruleId} with AI in {ShortName(repositoryFullName)}",
				WorkDescriptor.ForRepository(
					WorkKind.FixWithAiRule,
					organization,
					repositoryFullName,
					("ruleId", ruleId)),
				$"aifix:{repositoryFullName}:{ruleId}",
				null,
				consoleNodeKey);

			if (item is not null)
			{
				queued++;
			}
		}

		return queued;
	}

	/// <summary>The repository's name without its owner, for a title that has to stay readable.</summary>
	private static string ShortName(string repositoryFullName)
		=> repositoryFullName.Contains('/', StringComparison.Ordinal)
			? repositoryFullName.Split('/')[^1]
			: repositoryFullName;

	/// <summary>Queues a clone of every given candidate. Returns how many were queued.</summary>
	/// <param name="organization">The organisation they belong to.</param>
	/// <param name="targets">The repositories to clone.</param>
	/// <param name="consoleNodeKey">The console the output belongs to.</param>
	public int EnqueueClone(
		string organization,
		IReadOnlyList<RepositoryCloneCandidate> targets,
		string? consoleNodeKey)
	{
		var queued = 0;

		foreach (var target in targets)
		{
			var item = lanes.Enqueue(
				$"Clone {target.FullName}",
				WorkDescriptor.ForRepository(WorkKind.Clone, organization, target.FullName),
				$"clone:{target.FullName}",
				step: null,
				consoleNodeKey);

			if (item is not null)
			{
				queued++;
			}
		}

		return queued;
	}

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
				// Never folded, unlike the fix it follows. A second push costs nothing — there is simply
				// nothing to commit — whereas a folded push leaves the repository fixed and unpushed, with
				// an uncommitted change nobody is tracking. The asymmetry is why this one opts out.
				lanes.Enqueue(
					$"Commit & push {repositoryFullName}",
					WorkDescriptor.ForRepository(WorkKind.CommitAndPush, organization, repositoryFullName),
					$"commit-push:{repositoryFullName}:{ruleId}",
					WorkflowStep.CommitAndPush,
					consoleNodeKey,
					foldDuplicates: false);
			}
		}

		return queued;
	}
}
