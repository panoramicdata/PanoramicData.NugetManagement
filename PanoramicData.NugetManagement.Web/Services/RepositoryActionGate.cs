using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Decides which actions a repository that publishes no package may be put through.
/// </summary>
/// <remarks>
/// Every GitHub repository in a governed organisation is shown, including those that publish nothing
/// to NuGet. Those rows came from GitHub's list rather than from a package we publish, so they are
/// assess-only: they can be synced, assessed and tested, but nothing that edits files, builds, pushes,
/// closes pull requests or publishes is done to them. There is deliberately no opt-in yet — it would
/// need a persisted per-repository setting and UI of its own, and refusing is the safe default.
/// </remarks>
public static class RepositoryActionGate
{
	/// <summary>The sentence shown wherever an action is refused for this reason.</summary>
	public const string AssessOnlyReason =
		"This repository publishes no NuGet package, so it is assess-only: sync, assess and test are available; "
		+ "fixing, building, committing, pushing and publishing are not.";

	/// <summary>
	/// Whether a workflow step may act on the repository.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="step">The step.</param>
	public static bool Allows(RepositoryDashboardRow row, WorkflowStep step)
		=> !row.IsUnpackaged
			|| step is WorkflowStep.GitSync or WorkflowStep.Reassess or WorkflowStep.Test;

	/// <summary>
	/// Whether a queued piece of work may act on the repository.
	/// </summary>
	/// <param name="row">The repository.</param>
	/// <param name="kind">The work.</param>
	public static bool Allows(RepositoryDashboardRow row, WorkKind kind)
		=> !row.IsUnpackaged || !IsOutward(kind);

	/// <summary>
	/// Whether a kind of work changes files, a remote or a feed, as opposed to reading and assessing.
	/// </summary>
	/// <param name="kind">The work.</param>
	public static bool IsOutward(WorkKind kind) => kind is
		WorkKind.FixAll or WorkKind.FixCategory or WorkKind.FixRule
		or WorkKind.FixWithAiRule or WorkKind.FixWithAiIssue
		or WorkKind.TriageDependabot
		or WorkKind.Build or WorkKind.CommitAndPush or WorkKind.Publish;
}
