using Octokit;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// How GitHub's two ways of reporting CI become the three states a merge decision needs.
/// </summary>
/// <remarks>
/// GitHub reports CI as check runs (Actions, most apps) and, for older integrations, as commit statuses.
/// A repository whose only CI uses statuses would look like one with no CI at all if only check runs were
/// read, and would merge on the local gate alone, so both are read and mapped the same cautious way:
/// anything not clearly finished and fine does not pass.
/// </remarks>
public static class GitHubCheckMapping
{
	/// <summary>
	/// Maps one check run.
	/// </summary>
	/// <param name="status">Whether it is queued, running or completed.</param>
	/// <param name="conclusion">How it ended, when it has.</param>
	public static CheckRunConclusion FromCheckRun(CheckStatus status, CheckConclusion? conclusion)
	{
		if (status != CheckStatus.Completed)
		{
			return CheckRunConclusion.Pending;
		}

		return conclusion is CheckConclusion.Success or CheckConclusion.Neutral or CheckConclusion.Skipped
			? CheckRunConclusion.Passed
			: CheckRunConclusion.Failed;
	}

	/// <summary>
	/// Maps one legacy commit status.
	/// </summary>
	/// <param name="state">The status's state.</param>
	public static CheckRunConclusion FromStatus(CommitState state)
		=> state switch
		{
			CommitState.Success => CheckRunConclusion.Passed,
			CommitState.Pending => CheckRunConclusion.Pending,
			_ => CheckRunConclusion.Failed
		};
}
