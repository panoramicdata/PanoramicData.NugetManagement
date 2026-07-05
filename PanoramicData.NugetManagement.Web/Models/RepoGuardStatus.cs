namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// The state of a repository in the regression-guard build queue.
/// </summary>
public enum GuardState
{
	/// <summary>Waiting to be built.</summary>
	Queued,

	/// <summary>Currently building.</summary>
	Building,

	/// <summary>Built successfully after our change.</summary>
	Verified,

	/// <summary>Our change broke the build; our commits were reverted and pushed.</summary>
	RegressionReverted,

	/// <summary>Build is failing, but not because of our change (pre-existing) — left untouched.</summary>
	BuildFailingNotOurs,

	/// <summary>An error occurred while verifying or rolling back.</summary>
	Error
}

/// <summary>
/// The regression-guard status of a single repository whose build we may have dirtied.
/// </summary>
public sealed class RepoGuardStatus
{
	/// <summary>The repository full name (e.g. "panoramicdata/Highlight.Api").</summary>
	public required string RepositoryFullName { get; init; }

	/// <summary>The current state.</summary>
	public GuardState State { get; set; }

	/// <summary>A human-readable message.</summary>
	public string Message { get; set; } = string.Empty;

	/// <summary>When the state last changed (UTC).</summary>
	public DateTimeOffset UpdatedUtc { get; set; }
}
