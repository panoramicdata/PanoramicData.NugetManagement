using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Reads a GitHub Actions run's own status and conclusion strings into a <see cref="ReleaseRun"/>.
/// </summary>
/// <remarks>
/// Works from the strings GitHub sends rather than a client library's enums, for two reasons: a
/// conclusion GitHub adds later would otherwise arrive as whatever the library's default happens to
/// be, and Octokit's run type cannot be constructed in a test, which would leave this logic — the
/// only part with a decision in it — uncovered.
/// </remarks>
public static class ReleaseRunFactory
{
	/// <summary>
	/// Builds a release run from GitHub's own representation of it.
	/// </summary>
	/// <param name="tag">The tag whose run this is.</param>
	/// <param name="runId">The run id.</param>
	/// <param name="status">GitHub's run status (e.g. "in_progress", "completed").</param>
	/// <param name="conclusion">GitHub's run conclusion, or null while the run is unfinished.</param>
	/// <param name="htmlUrl">The run's page.</param>
	/// <param name="startedAt">When the run started.</param>
	/// <param name="updatedAt">When the run was last updated — its completion time once finished.</param>
	public static ReleaseRun From(
		string tag,
		long runId,
		string? status,
		string? conclusion,
		string? htmlUrl,
		DateTimeOffset? startedAt,
		DateTimeOffset? updatedAt)
	{
		var runStatus = ReadStatus(status);
		var completed = runStatus is ReleaseRunStatus.Completed;

		return new ReleaseRun
		{
			TagRef = tag,
			RunId = runId,
			Status = runStatus,
			// Only a finished run has a conclusion. Reading one from an unfinished run would make it
			// indistinguishable from a failure, and GitHub sends null for it anyway.
			Conclusion = completed ? ReadConclusion(conclusion) : null,
			HtmlUrl = htmlUrl,
			StartedAtUtc = startedAt,
			// updated_at moves while a run is going, so it is a completion time only once the run has
			// completed. Taken earlier it would put the run "finished" seconds ago and let CI-11 grant
			// an indexing grace to a release that has published nothing.
			CompletedAtUtc = completed ? updatedAt : null
		};
	}

	/// <summary>
	/// Reads a run status, treating anything unrecognised as still running: a status this does not
	/// know must never present itself as a finished run, or CI-13 reports a release that has not
	/// finished as one that failed.
	/// </summary>
	private static ReleaseRunStatus ReadStatus(string? status) => status switch
	{
		"completed" => ReleaseRunStatus.Completed,
		"queued" or "pending" or "waiting" or "requested" => ReleaseRunStatus.Queued,
		_ => ReleaseRunStatus.InProgress
	};

	/// <summary>
	/// Reads a conclusion. Anything that is not plainly a success is one of the failure states: a run
	/// that ended as "action required", "startup failure", or a conclusion GitHub has added since
	/// published nothing, and reading it as a success would hide exactly the failure CI-13 exists to
	/// find.
	/// </summary>
	private static ReleaseRunConclusion ReadConclusion(string? conclusion) => conclusion switch
	{
		"success" => ReleaseRunConclusion.Success,
		"failure" => ReleaseRunConclusion.Failure,
		"cancelled" => ReleaseRunConclusion.Cancelled,
		"timed_out" => ReleaseRunConclusion.TimedOut,
		_ => ReleaseRunConclusion.Other
	};
}
