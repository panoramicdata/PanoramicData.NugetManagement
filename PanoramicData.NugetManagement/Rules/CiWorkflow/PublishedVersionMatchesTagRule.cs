using NuGet.Versioning;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the newest version tag actually reached nuget.org, catching a release that was tagged
/// but never published.
/// </summary>
/// <remarks>
/// Publish.ps1 pushes a tag, says CI will do the rest, and exits. Until CI-09 gained verification,
/// nothing checked the run, so a failed publish looked exactly like a successful one. A sweep on
/// 2026-08-28 found nine repositories in that state, the worst 24 versions behind and several failing
/// since June — each for a different reason, none of them visible. This rule needs no release to
/// happen before it can report: it compares what is tagged with what is published, every assessment.
/// <para>
/// Which is also how it came to report a release that was working. A tag is ahead of nuget.org for a
/// few minutes every time a release happens, and once for HaloPsa.Api 2.196.75 that window was where
/// an assessment landed: the run had succeeded, the package was pushed, and only the version index
/// had yet to catch up. The run for the tag is what tells those apart, so this rule now consults it
/// and reports only what it cannot explain — a failed run belongs to CI-13, which can say what went
/// wrong.
/// </para>
/// </remarks>
public class PublishedVersionMatchesTagRule : RuleBase, IFixedOutsideTheWorkingTree
{
	/// <summary>
	/// How long after a successful run the package is allowed to be missing from nuget.org before
	/// that counts as a failure.
	/// </summary>
	/// <remarks>
	/// The version index is the fastest view nuget.org offers and it still lags a push. HaloPsa.Api
	/// 2.196.75 was pushed by a run that succeeded at 12:46 and read as unpublished a minute later.
	/// Half an hour is far longer than the lag observed and far shorter than the months the estate's
	/// stuck releases went unnoticed, so it swallows the former without hiding the latter.
	/// </remarks>
	private static readonly TimeSpan _indexingGrace = TimeSpan.FromMinutes(30);

	private readonly TimeProvider _timeProvider;

	/// <summary>
	/// Initializes a new instance of the <see cref="PublishedVersionMatchesTagRule"/> class.
	/// </summary>
	public PublishedVersionMatchesTagRule()
		: this(TimeProvider.System)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="PublishedVersionMatchesTagRule"/> class.
	/// </summary>
	/// <param name="timeProvider">Clock used to measure how long ago a successful run finished.</param>
	public PublishedVersionMatchesTagRule(TimeProvider timeProvider)
	{
		_timeProvider = timeProvider;
	}

	/// <inheritdoc />
	public override string RuleId => "CI-11";

	/// <inheritdoc />
	public override string RuleName => "Published version keeps up with the newest tag";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — nothing to publish."));
		}

		if (context.LatestTag is null)
		{
			return Task.FromResult(NotApplicable(
				"No version tag known; the repository is not cloned locally, or has never been tagged."));
		}

		if (!TryParseVersion(context.LatestTag, out var tagged))
		{
			return Task.FromResult(NotApplicable(
				$"The newest tag '{context.LatestTag}' is not a version, so there is nothing to compare."));
		}

		// A run that explains the gap is the difference between a release that is happening and a
		// release that did not happen. Checked before the version comparison because every branch
		// below it would otherwise report a failure that has not occurred yet.
		if (ExplainedByTheReleaseRun(context, out var explanation))
		{
			return Task.FromResult(explanation);
		}

		// Nothing published at all, yet a version has been tagged: the most complete form of the
		// failure this rule looks for, not an absence of information.
		if (context.LatestPublishedVersion is null)
		{
			return Task.FromResult(Fail(
				$"Tag {context.LatestTag} exists but no version of this package has been published.",
				CreateAdvisory(context, context.LatestTag, published: null)));
		}

		if (!TryParseVersion(context.LatestPublishedVersion, out var published))
		{
			return Task.FromResult(NotApplicable(
				$"The published version '{context.LatestPublishedVersion}' could not be read as a version."));
		}

		// Published ahead of the newest local tag means a stale clone, not a failed release.
		return Task.FromResult(tagged <= published
			? Pass($"Published {context.LatestPublishedVersion} is up to date with tag {context.LatestTag}.")
			: Fail(
				$"Tag {context.LatestTag} was pushed but nuget.org still has {context.LatestPublishedVersion}.",
				CreateAdvisory(context, context.LatestTag, context.LatestPublishedVersion)));
	}

	/// <summary>
	/// Whether the run for the newest tag accounts for the package not being on nuget.org yet, and
	/// the result to return when it does.
	/// </summary>
	/// <remarks>
	/// Three of the four states a run can be in are not this rule's finding. Only a run that
	/// succeeded long enough ago for nuget.org to have indexed it — or no run information at all —
	/// leaves the version comparison as the last word.
	/// </remarks>
	private bool ExplainedByTheReleaseRun(RepositoryContext context, out RuleResult result)
	{
		var run = context.ReleaseRun;

		if (run is null)
		{
			result = null!;
			return false;
		}

		if (run.InFlight)
		{
			result = Pass(
				$"Release {context.LatestTag} is in flight — run {run.RunId} has not finished yet.");
			return true;
		}

		if (run.Failed)
		{
			// Left to CI-13, which can say what went wrong. Reporting it here as well would bill one
			// broken release as two findings.
			result = NotApplicable(
				$"Release {context.LatestTag} did not complete — see CI-13 for run {run.RunId}.");
			return true;
		}

		var completedAt = run.CompletedAtUtc;
		if (run.Succeeded && completedAt is not null
			&& _timeProvider.GetUtcNow() - completedAt.Value < _indexingGrace)
		{
			result = Pass(
				$"Release {context.LatestTag} published by run {run.RunId} — nuget.org's version index "
				+ "is still catching up.");
			return true;
		}

		result = null!;
		return false;
	}

	private static RuleAdvisory CreateAdvisory(RepositoryContext context, string tag, string? published)
		=> new()
		{
			Summary = published is null
				? $"Tag {tag} never published — find out why the release run did not produce a package."
				: $"Tag {tag} never published — nuget.org is still on {published}.",
			Detail = $"""
				A tag was pushed but the package did not reach nuget.org, so the release did not complete.
				The tag being present is not evidence that it did: until CI-09 gained verification, the
				publish script reported success as soon as the tag was pushed.

				Start with the run for the tag:

				```
				gh run list --repo {context.FullName} --branch {tag} --limit 5
				gh run view <id> --repo {context.FullName} --log-failed
				```

				If the run shows no failed step, read the check-run annotation — a refused job (an
				exhausted Actions budget, for instance) fails before any step runs and only says so
				there:

				```
				gh api repos/{context.FullName}/actions/runs/<id>/jobs --jq '.jobs[0].id'
				gh api repos/{context.FullName}/check-runs/<job-id>/annotations --jq '.[0].message'
				```

				Causes seen across the estate, all different, all invisible until someone looked: an
				exhausted Actions budget refusing every job; trusted publishing not configured, giving
				`Token exchange failed (HTTP 401)`; a filename case mismatch that only breaks on
				ubuntu-latest; integration tests in CI needing credentials the runner does not have; a
				workflow pinned to an action version that does not exist; and an ordinary compile error.

				Once the cause is fixed, re-run the release: `gh run rerun <id> --repo {context.FullName} --failed`.
				""",
			Data = new()
			{
				["latest_tag"] = tag,
				["latest_published_version"] = published ?? string.Empty,
				["repository"] = context.FullName
			}
		};

	/// <summary>
	/// Reads a version from a tag, tolerating the common leading "v".
	/// </summary>
	private static bool TryParseVersion(string value, out NuGetVersion version)
		=> NuGetVersion.TryParse(value.TrimStart('v', 'V'), out version!);
}
