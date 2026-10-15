using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Web.Services;

public sealed partial class WorkExecutors
{
	/// <summary>
	/// Hands edits a model session has already made in a clone to the AI change pipeline.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="kind">What produced the edits.</param>
	/// <param name="label">What the edits are for, for the branch name, for example <c>rule CI-15</c>.</param>
	/// <param name="title">The pull request title.</param>
	/// <param name="writtenPaths">The repository-relative paths the session wrote.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	/// <returns>How delivery ended.</returns>
	/// <remarks>
	/// A rule fix is delivered without the path allowlist or cap an issue fix gets: its edits are bounded by
	/// its playbook and by the toolbox, which cannot write outside the clone, and its prompt comes from an
	/// assessment rather than from a stranger's text.
	/// </remarks>
	private async Task<AiChangeOutcome> DeliverAiChangeAsync(
		RepositoryDashboardRow row,
		AiChangeKind kind,
		string label,
		string title,
		IReadOnlyCollection<string> writtenPaths,
		CancellationToken cancellationToken)
	{
		var parts = row.RepositoryFullName.Split('/');

		var pipeline = AiChangePipelineFactory.Create(
			new LocalRepoAiChangeGit(row.LocalPath!),
			new DashboardAiChangeTests(dashboard, localRepo, row, row.RepositoryFullName),
			new DeliveryOnlyWork(),
			new OctokitGitHubWriteApi(await CreateGitHubClientAsync().ConfigureAwait(false)),
			_ => true,
			int.MaxValue);

		var request = new AiChangeRequest(
			parts[0],
			parts[^1],
			kind,
			AiChangePipelineFactory.Slug(label, Guid.NewGuid().ToString("N")[..6]),
			title,
			IssueNumber: null,
			RequiresRedTest: false);

		return await pipeline
			.DeliverAsync(request, writtenPaths.ToHashSet(StringComparer.OrdinalIgnoreCase), Say, cancellationToken)
			.ConfigureAwait(false);
	}
}
