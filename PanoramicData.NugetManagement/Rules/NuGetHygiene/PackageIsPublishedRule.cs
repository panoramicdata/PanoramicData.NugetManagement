using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a package a repository declares has actually been published to nuget.org.
/// </summary>
/// <remarks>
/// A repository can be wired to publish — a PackageId, a pack step, a publish workflow — and still
/// have never shipped a version. The common causes are a tag that never triggered the workflow, a
/// trusted-publishing policy that does not match, and an identifier nuget.org refuses. The last two
/// are easy to miss because a push using <c>--skip-duplicate</c> reports a nuget.org Conflict as
/// success, so the run is green and nothing was published. This rule asks nuget.org directly.
/// </remarks>
public class PackageIsPublishedRule : RuleBase
{
	private readonly Func<string, CancellationToken, Task<bool?>> _publishedResolver;

	/// <summary>
	/// Initializes a new instance of the <see cref="PackageIsPublishedRule"/> class.
	/// </summary>
	public PackageIsPublishedRule()
		: this(new NuGetPackageExistenceChecker().IsPublishedAsync)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="PackageIsPublishedRule"/> class.
	/// </summary>
	/// <param name="publishedResolver">
	/// Says whether nuget.org holds any version of a package: true or false, or null when unknown.
	/// </param>
	public PackageIsPublishedRule(Func<string, CancellationToken, Task<bool?>> publishedResolver)
	{
		_publishedResolver = publishedResolver;
	}

	/// <inheritdoc />
	public override string RuleId => "PKG-14";

	/// <inheritdoc />
	public override string RuleName => "Declared package is published to nuget.org";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var earlyResult = PackagingCheckApplies(context, out var packableProjects);
		if (earlyResult is not null)
		{
			return earlyResult;
		}

		var unpublished = new List<string>();
		foreach (var packageId in ResolvePackageIds(context, packableProjects))
		{
			cancellationToken.ThrowIfCancellationRequested();

			// Unknown is not unpublished: a nuget.org outage must not read as a repository defect.
			if (await _publishedResolver(packageId, cancellationToken).ConfigureAwait(false) == false)
			{
				unpublished.Add(packageId);
			}
		}

		if (unpublished.Count == 0)
		{
			return Pass("Every package this repository declares is published to nuget.org.");
		}

		unpublished.Sort(StringComparer.OrdinalIgnoreCase);

		return Fail(
			$"Declared package(s) never published to nuget.org: {string.Join(", ", unpublished)}.",
			new RuleAdvisory
			{
				Summary = "Publish the package, or stop declaring it.",
				Detail = $$"""
					nuget.org has no version of:
					{{string.Join("\n", unpublished.Select(packageId => $"- `{packageId}`"))}}

					This repository declares that it publishes these packages but none has ever shipped.
					Work through the usual causes:

					- No release was ever tagged. Run `Publish.ps1` from a clean, up-to-date `main` so the
					  tag matches the version Nerdbank.GitVersioning computes.
					- The publish run was green but pushed nothing. A push using `--skip-duplicate` reports
					  a nuget.org `Conflict` as success, so read the "Pushing ..." line in the publish log.
					- A `Conflict` that repeats for every version means nuget.org refuses the identifier —
					  it is owned by someone else or reserved. Rename the package, as
					  `PanoramicData.LanSweeper.Api` did.
					- Trusted publishing has no policy matching this repository and workflow file.

					If the package was never meant to ship, set `<IsPackable>false</IsPackable>` so the
					repository stops claiming it does.
					""",
				// Deliberately no remediation_type: releasing is an outward-facing act that cannot be
				// undone, so it is described here and left for a human to carry out.
				Data = new()
				{
					["unpublished_packages"] = unpublished.ToArray()
				}
			});
	}
}
