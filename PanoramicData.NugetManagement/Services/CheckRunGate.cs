namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// How one check run on a pull request's head commit stands.
/// </summary>
public enum CheckRunConclusion
{
	/// <summary>Queued or still running.</summary>
	Pending,

	/// <summary>Finished successfully, was neutral, or was skipped.</summary>
	Passed,

	/// <summary>Finished unsuccessfully, was cancelled, or timed out.</summary>
	Failed
}

/// <summary>
/// One check run.
/// </summary>
/// <param name="Name">The check's name, for saying which one failed.</param>
/// <param name="Conclusion">Where it stands.</param>
public sealed record CheckRunState(string Name, CheckRunConclusion Conclusion);

/// <summary>
/// What a pull request's check runs add up to.
/// </summary>
public enum CheckGateResult
{
	/// <summary>No check runs exist (yet).</summary>
	NoChecks,

	/// <summary>None has failed and at least one has not finished.</summary>
	Pending,

	/// <summary>Every check finished and none failed.</summary>
	Passed,

	/// <summary>At least one check failed.</summary>
	Failed
}

/// <summary>
/// Reduces check runs to the one answer a merge decision needs.
/// </summary>
public static class CheckRunGate
{
	/// <summary>
	/// Adds up a set of check runs.
	/// </summary>
	/// <param name="runs">The check runs on the pull request's head commit.</param>
	/// <returns>The combined result; a failure wins over pending.</returns>
	public static CheckGateResult Evaluate(IReadOnlyList<CheckRunState> runs)
	{
		if (runs.Count == 0)
		{
			return CheckGateResult.NoChecks;
		}

		if (runs.Any(run => run.Conclusion == CheckRunConclusion.Failed))
		{
			return CheckGateResult.Failed;
		}

		return runs.Any(run => run.Conclusion == CheckRunConclusion.Pending)
			? CheckGateResult.Pending
			: CheckGateResult.Passed;
	}
}
