namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// The outcome of applying a fix across one repository during a bulk operation.
/// </summary>
public enum RepoApplyStatus
{
	/// <summary>The fix was applied, committed and pushed.</summary>
	Pushed,

	/// <summary>Nothing needed doing (already resolved after sync, or no file changes).</summary>
	NothingToDo,

	/// <summary>No automated remediation exists for this issue.</summary>
	NoRemediation,

	/// <summary>The operation failed for this repository.</summary>
	Failed
}

/// <summary>
/// The result of a bulk apply for a single repository.
/// </summary>
public sealed class RepoApplyResult
{
	/// <summary>The repository full name.</summary>
	public required string RepositoryFullName { get; init; }

	/// <summary>The outcome for this repository.</summary>
	public required RepoApplyStatus Status { get; init; }

	/// <summary>A human-readable message.</summary>
	public required string Message { get; init; }
}

/// <summary>
/// The aggregate outcome of a bulk apply-across-repositories operation.
/// </summary>
public sealed class BulkApplyOutcome
{
	/// <summary>Per-repository results, in the order processed.</summary>
	public List<RepoApplyResult> Results { get; } = [];

	/// <summary>Whether the operation stopped early because a repository failed.</summary>
	public bool StoppedOnFailure { get; set; }

	/// <summary>The number of repositories successfully committed &amp; pushed.</summary>
	public int PushedCount => Results.Count(r => r.Status == RepoApplyStatus.Pushed);

	/// <summary>The number of repositories that failed.</summary>
	public int FailedCount => Results.Count(r => r.Status == RepoApplyStatus.Failed);
}
