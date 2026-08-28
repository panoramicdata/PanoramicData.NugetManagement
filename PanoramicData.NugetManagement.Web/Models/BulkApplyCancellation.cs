namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// How far a bulk apply had got with one repository. The boundary that matters is the commit: before
/// it, the work can be undone; after it, the change has left the machine.
/// </summary>
public enum RepoApplyPhase
{
	/// <summary>Not reached yet. Nothing has been written.</summary>
	NotStarted,

	/// <summary>Part-way through writing remediations into the working tree.</summary>
	Applying,

	/// <summary>Remediations are written but not yet committed.</summary>
	Applied,

	/// <summary>Committed and pushed. The change stands.</summary>
	Pushed
}

/// <summary>
/// Decides what stopping a bulk apply means for the repository it was part-way through, so that a
/// change is atomic per repository: either fully applied or not applied at all.
/// </summary>
public static class BulkApplyCancellation
{
	/// <summary>
	/// Whether stopping at this phase has left changes in the working tree that must be undone before
	/// the clone is fit to use again.
	/// </summary>
	public static bool NeedsRevert(RepoApplyPhase phase)
		=> phase is RepoApplyPhase.Applying or RepoApplyPhase.Applied;

	/// <summary>
	/// The outcome to record for a repository the user stopped on.
	/// </summary>
	/// <param name="repositoryFullName">The repository the run was part-way through.</param>
	/// <param name="phase">How far it had got.</param>
	public static RepoApplyResult Describe(string repositoryFullName, RepoApplyPhase phase) => new()
	{
		RepositoryFullName = repositoryFullName,
		Status = phase switch
		{
			RepoApplyPhase.Applying or RepoApplyPhase.Applied => RepoApplyStatus.Reverted,
			RepoApplyPhase.Pushed => RepoApplyStatus.Pushed,
			_ => RepoApplyStatus.Skipped
		},
		Message = phase switch
		{
			RepoApplyPhase.Applying or RepoApplyPhase.Applied => "Stopped before the commit; local changes were reverted.",
			RepoApplyPhase.Pushed => "Stopped after this repository was pushed; the change stands.",
			_ => "Stopped before this repository was started; nothing was touched."
		}
	};
}
