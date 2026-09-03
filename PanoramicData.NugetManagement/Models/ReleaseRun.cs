namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// How far the CI run for a release tag has got.
/// </summary>
public enum ReleaseRunStatus
{
	/// <summary>
	/// Accepted but not started.
	/// </summary>
	Queued,

	/// <summary>
	/// Running now.
	/// </summary>
	InProgress,

	/// <summary>
	/// Finished, with a <see cref="ReleaseRun.Conclusion"/>.
	/// </summary>
	Completed
}

/// <summary>
/// How the CI run for a release tag ended.
/// </summary>
public enum ReleaseRunConclusion
{
	/// <summary>
	/// Every job succeeded.
	/// </summary>
	Success,

	/// <summary>
	/// A job failed.
	/// </summary>
	Failure,

	/// <summary>
	/// Cancelled, by a person or by a newer run.
	/// </summary>
	Cancelled,

	/// <summary>
	/// Ran out of time.
	/// </summary>
	TimedOut,

	/// <summary>
	/// Ended some other way — skipped, action required, neutral, or a conclusion GitHub has added
	/// since. Not a success, so a release rule treats it as a release that did not happen.
	/// </summary>
	Other
}

/// <summary>
/// The CI run for a repository's newest release tag, as far as it is known.
/// </summary>
/// <remarks>
/// This exists so that a rule can tell three states apart that look identical from the version
/// numbers alone: a release still in flight, a release that has landed but is not yet indexed by
/// nuget.org, and a release that failed. Until it existed, CI-11 reported all three as the last one.
/// A null <see cref="RepositoryContext.ReleaseRun"/> means nothing is known — no GitHub client on
/// the local assess path, or no run for the tag — which is not evidence of anything.
/// </remarks>
public class ReleaseRun
{
	/// <summary>
	/// The tag whose run this is.
	/// </summary>
	public required string TagRef { get; init; }

	/// <summary>
	/// The GitHub Actions run id.
	/// </summary>
	public required long RunId { get; init; }

	/// <summary>
	/// How far the run has got.
	/// </summary>
	public required ReleaseRunStatus Status { get; init; }

	/// <summary>
	/// How the run ended, or null while it is still going.
	/// </summary>
	public ReleaseRunConclusion? Conclusion { get; init; }

	/// <summary>
	/// The run's page on GitHub, where the cause of a failure can be read.
	/// </summary>
	public string? HtmlUrl { get; init; }

	/// <summary>
	/// When the run started.
	/// </summary>
	public DateTimeOffset? StartedAtUtc { get; init; }

	/// <summary>
	/// When the run finished, or null while it is still going.
	/// </summary>
	public DateTimeOffset? CompletedAtUtc { get; init; }

	/// <summary>
	/// Whether the run finished without succeeding — the state CI-13 reports.
	/// </summary>
	public bool Failed
		=> Status is ReleaseRunStatus.Completed && Conclusion is not ReleaseRunConclusion.Success;

	/// <summary>
	/// Whether the run has yet to reach a conclusion, whether or not it has started.
	/// </summary>
	public bool InFlight => Status is ReleaseRunStatus.Queued or ReleaseRunStatus.InProgress;

	/// <summary>
	/// Whether the run succeeded.
	/// </summary>
	public bool Succeeded
		=> Status is ReleaseRunStatus.Completed && Conclusion is ReleaseRunConclusion.Success;
}
