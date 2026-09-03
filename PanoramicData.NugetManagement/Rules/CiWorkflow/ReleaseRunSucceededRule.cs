using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the CI run for the newest release tag succeeded, catching a release that failed in CI
/// rather than one that merely has not landed yet.
/// </summary>
/// <remarks>
/// CI-11 can see that a tag never reached nuget.org, but not why, and it has to stay quiet while a
/// release is still in flight — which left a run that failed thirty seconds ago reported by nothing
/// at all, and a run that failed months ago reported only as a version mismatch. This rule reads the
/// run for the tag and reports its conclusion, so the finding names the failure instead of its
/// symptom.
/// </remarks>
public class ReleaseRunSucceededRule : RuleBase, IFixedOutsideTheWorkingTree
{
	/// <inheritdoc />
	public override string RuleId => "CI-13";

	/// <inheritdoc />
	public override string RuleName => "Release run succeeded";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — there is no release to run."));
		}

		if (context.LatestTag is null)
		{
			return Task.FromResult(NotApplicable(
				"No version tag known; the repository is not cloned locally, or has never been tagged."));
		}

		// Nothing known about the run is not evidence that it failed: the local assess path may have
		// had no GitHub client, and a tag pushed without a workflow has no run at all. CI-11 still
		// reports the unpublished tag in both cases.
		if (context.ReleaseRun is not { } run)
		{
			return Task.FromResult(NotApplicable(
				$"No CI run is known for tag {context.LatestTag}."));
		}

		if (run.InFlight)
		{
			return Task.FromResult(NotApplicable(
				$"Run {run.RunId} for tag {context.LatestTag} has not finished, so it has no conclusion yet."));
		}

		return Task.FromResult(run.Succeeded
			? Pass($"Run {run.RunId} for tag {context.LatestTag} succeeded.")
			: Fail(
				$"The release run for tag {context.LatestTag} did not succeed: run {run.RunId} ended as "
				+ $"{Describe(run.Conclusion)}.",
				CreateAdvisory(context, context.LatestTag, run)));
	}

	private static RuleAdvisory CreateAdvisory(RepositoryContext context, string tag, ReleaseRun run)
		=> new()
		{
			Summary = $"Release {tag} failed in CI — run {run.RunId} ended as {Describe(run.Conclusion)}.",
			Detail = $"""
				The tag was pushed and CI ran, but the run did not succeed, so no package was
				published. Publish.ps1 pushes the tag, says CI will do the rest, and exits, so nothing
				reported this at the time it happened.

				Read the failure:

				```
				gh run view {run.RunId} --repo {context.FullName} --log-failed
				```

				If the run shows no failed step, read the check-run annotation — a refused job (an
				exhausted Actions budget, for instance) fails before any step runs and only says so
				there:

				```
				gh api repos/{context.FullName}/actions/runs/{run.RunId}/jobs --jq '.jobs[0].id'
				gh api repos/{context.FullName}/check-runs/<job-id>/annotations --jq '.[0].message'
				```

				Causes seen across the estate, all different, all invisible until someone looked: an
				exhausted Actions budget refusing every job; trusted publishing not configured, giving
				`Token exchange failed (HTTP 401)`; a filename case mismatch that only breaks on
				ubuntu-latest; integration tests in CI needing credentials the runner does not have; a
				workflow pinned to an action version that does not exist; and an ordinary compile error.

				Once the cause is fixed, re-run the release:
				`gh run rerun {run.RunId} --repo {context.FullName} --failed`.
				""",
			Data = new()
			{
				["latest_tag"] = tag,
				["run_id"] = run.RunId.ToString(),
				["run_conclusion"] = Describe(run.Conclusion),
				["run_url"] = run.HtmlUrl ?? $"https://github.com/{context.FullName}/actions/runs/{run.RunId}",
				["repository"] = context.FullName
			}
		};

	/// <summary>
	/// Names a conclusion the way GitHub does, so the message matches what the run page shows.
	/// </summary>
	private static string Describe(ReleaseRunConclusion? conclusion) => conclusion switch
	{
		ReleaseRunConclusion.Failure => "failure",
		ReleaseRunConclusion.Cancelled => "cancelled",
		ReleaseRunConclusion.TimedOut => "timed out",
		ReleaseRunConclusion.Success => "success",
		_ => "neither success nor failure"
	};
}
