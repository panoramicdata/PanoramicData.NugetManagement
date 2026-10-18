using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Web.Services;

public sealed partial class WorkExecutors
{
	/// <summary>
	/// Hands edits a model session has already made in a clone to the AI change pipeline.
	/// </summary>
	/// <param name="item">The work item, which keeps a copy of the change for the Changes tab.</param>
	/// <param name="row">The repository.</param>
	/// <param name="kind">What produced the edits.</param>
	/// <param name="label">What the edits are for, for the branch name, for example <c>rule CI-15</c>.</param>
	/// <param name="title">The pull request title.</param>
	/// <param name="writtenPaths">The repository-relative paths the session wrote.</param>
	/// <param name="knownFailures">Tests that already failed before the session, which do not count against it.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	/// <returns>How delivery ended.</returns>
	/// <remarks>
	/// A rule fix is delivered without the path allowlist or cap an issue fix gets: its edits are bounded by
	/// its playbook and by the toolbox, which cannot write outside the clone, and its prompt comes from an
	/// assessment rather than from a stranger's text.
	/// </remarks>
	private async Task<AiChangeOutcome> DeliverAiChangeAsync(
		WorkItem item,
		RepositoryDashboardRow row,
		AiChangeKind kind,
		string label,
		string title,
		IReadOnlyCollection<string> writtenPaths,
		IReadOnlySet<string> knownFailures,
		CancellationToken cancellationToken)
	{
		var parts = row.RepositoryFullName.Split('/');

		var pipeline = AiChangePipelineFactory.Create(
			new LocalRepoAiChangeGit(row.LocalPath!, row.RepositoryFullName),
			new DashboardAiChangeTests(dashboard, localRepo, row, row.RepositoryFullName, knownFailures),
			new DeliveryOnlyWork(),
			new OctokitGitHubWriteApi(await CreateGitHubClientAsync().ConfigureAwait(false)),
			AiChangePipelineFactory.RuleFixLimits(),
			KeepDeliveredChange(item, row.LocalPath!));

		var request = new AiChangeRequest(
			parts[0],
			parts[^1],
			kind,
			AiChangePipelineFactory.Slug(label, Guid.NewGuid().ToString("N")[..6]),
			title,
			IssueNumber: null,
			RequiresRedTest: false);

		var outcome = await pipeline
			.DeliverAsync(request, writtenPaths.ToHashSet(StringComparer.OrdinalIgnoreCase), Say, cancellationToken)
			.ConfigureAwait(false);

		NoteDeliveryOutcome(item, row, outcome);

		return outcome;
	}

	/// <summary>
	/// A hook for the pipeline that saves the change set on the work item just before the clone is reset.
	/// </summary>
	/// <param name="item">The work item.</param>
	/// <param name="clonePath">The clone's directory.</param>
	/// <remarks>
	/// Runs git, so it goes on the thread pool. The Changes tab reads the live clone, and the pipeline returns
	/// the clone to <c>main</c> afterwards, so without this copy a delivered fix would show an empty tab.
	/// </remarks>
	private static Func<CancellationToken, Task> KeepDeliveredChange(WorkItem item, string clonePath)
		=> cancellationToken => Task.Run(
			() => item.Delivered = DeliveredChange.Capture(new CloneChangeReader(), clonePath),
			cancellationToken);

	/// <summary>Adds how delivery ended, and the pull request link, to the copy kept on the item.</summary>
	/// <param name="item">The work item.</param>
	/// <param name="row">The repository.</param>
	/// <param name="outcome">What the pipeline reported.</param>
	private static void NoteDeliveryOutcome(WorkItem item, RepositoryDashboardRow row, AiChangeOutcome outcome)
		=> item.Delivered = item.Delivered?.WithOutcome(outcome, row.RepositoryFullName);
}
