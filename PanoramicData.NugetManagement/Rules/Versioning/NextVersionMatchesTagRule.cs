using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Reports work that is committed but not released: what Nerdbank.GitVersioning would version this
/// working tree as, against the newest tag it carries.
/// </summary>
/// <remarks>
/// Informational, because being in this state is normal — every repository with a commit since its
/// last release is in it, and a dashboard that reported that in red would be wrong about every
/// repository anyone is working in. What makes it worth saying at all is that nothing else says it:
/// CI-11 compares a tag with nuget.org and is silent about commits that were never tagged, so work
/// that is finished, merged and simply never released is invisible.
/// <para>
/// The finding is only true of a clone that matches origin, which is why it insists on that first.
/// From inside a clone, unreleased work and a stale checkout look identical: Divoom.Api and
/// Uk.Parliament both sat several releases behind origin on 2026-09-12, and the commits past their
/// newest local tags had long since been released by someone else. Reported as unreleased work, that
/// is an invention — and it would have been the estate's usual case rather than an edge one.
/// </para>
/// </remarks>
public class NextVersionMatchesTagRule : RuleBase, IFixedOutsideTheWorkingTree
{
	/// <inheritdoc />
	public override string RuleId => "VER-05";

	/// <inheritdoc />
	public override string RuleName => "Committed work has been released";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Versioning;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — there is nothing to release."));
		}

		if (context.NextVersion is null)
		{
			return Task.FromResult(NotApplicable(
				"The version this working tree would release could not be established."));
		}

		if (context.LatestTag is null)
		{
			return Task.FromResult(NotApplicable(
				"No version tag known; the repository is not cloned locally, or has never been released."));
		}

		// A feature branch's height counts unmerged commits, which are not waiting to be released.
		if (context.CurrentBranch is { } branch
			&& !branch.Equals(context.DefaultBranch, StringComparison.OrdinalIgnoreCase))
		{
			return Task.FromResult(NotApplicable(
				$"On branch {branch} rather than {context.DefaultBranch}, where unreleased work would be."));
		}

		if (!context.IsConfirmedInSyncWithOrigin)
		{
			// Said before the versions are compared, because the comparison would otherwise report a
			// stale clone's own commits as work waiting to be released.
			return Task.FromResult(NotApplicable(
				"The clone is not known to match origin, so commits past the newest local tag may "
				+ "already have been released. Sync to find out."));
		}

		if (!TryParseVersion(context.LatestTag, out var tagged))
		{
			return Task.FromResult(NotApplicable(
				$"The newest tag '{context.LatestTag}' is not a version, so there is nothing to compare."));
		}

		if (!TryParseVersion(context.NextVersion, out var next))
		{
			return Task.FromResult(NotApplicable(
				$"The computed version '{context.NextVersion}' could not be read as a version."));
		}

		// Tagged ahead of the working tree is a clone that has the tags but not the commits behind
		// them. Nothing here is unreleased.
		return next <= tagged
			? Task.FromResult(Pass(
				$"Everything committed here is released: the newest tag is {context.LatestTag}."))
			: Task.FromResult(Fail(
				$"This working tree would release {context.NextVersion}, but the newest tag is "
				+ $"{context.LatestTag} — that work is committed and not released.",
				CreateAdvisory(context, context.LatestTag, context.NextVersion)));
	}

	private static RuleAdvisory CreateAdvisory(RepositoryContext context, string tag, string nextVersion)
		=> new()
		{
			Summary = $"{context.Name} has work committed past {tag}; releasing it would publish {nextVersion}.",
			Detail = $"""
				Commits have landed on {context.DefaultBranch} since {tag} was tagged, so the code on
				nuget.org is older than the code in this repository. Nothing is wrong: this is what
				every repository looks like between releases, and it is reported for information rather
				than as a fault.

				Release it when it is ready:

				```
				./Publish.ps1
				```

				That tags {nextVersion} and lets CI publish it. CI-13 reports the run if it does not
				succeed, and CI-11 reports the package if it never reaches nuget.org.

				If the work is not meant to be released yet, there is nothing to do.
				""",
			Data = new()
			{
				["latest_tag"] = tag,
				["next_version"] = nextVersion,
				["repository"] = context.FullName
			}
		};
}
