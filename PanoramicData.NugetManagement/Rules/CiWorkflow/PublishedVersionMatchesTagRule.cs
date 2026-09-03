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
/// </remarks>
public class PublishedVersionMatchesTagRule : RuleBase, IFixedOutsideTheWorkingTree
{
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
