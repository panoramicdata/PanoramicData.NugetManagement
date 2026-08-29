using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a repository whose own package is deprecated on nuget.org has been archived.
/// </summary>
/// <remarks>
/// Deprecating a package and archiving its repository are two halves of one decision, and only the
/// first is visible on nuget.org. Archived repositories are filtered out of assessment, so a
/// repository that reaches this rule at all is still being governed — if its package is deprecated,
/// the archive step was missed. That is the finding: not the deprecation, which was deliberate, but
/// the repository outliving it and continuing to accrue remediations nobody will ever consume.
/// </remarks>
public class DeprecatedPackageRepositoryArchivedRule : RuleBase
{
	private readonly Func<string, string?, CancellationToken, Task<PackageDeprecationStatus?>> _deprecationResolver;

	/// <summary>
	/// Initializes a new instance of the <see cref="DeprecatedPackageRepositoryArchivedRule"/> class.
	/// </summary>
	public DeprecatedPackageRepositoryArchivedRule()
		: this(new NuGetDeprecationChecker().GetDeprecationAsync)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="DeprecatedPackageRepositoryArchivedRule"/> class.
	/// </summary>
	/// <param name="deprecationResolver">Resolves the deprecation status of a package version.</param>
	public DeprecatedPackageRepositoryArchivedRule(
		Func<string, string?, CancellationToken, Task<PackageDeprecationStatus?>> deprecationResolver)
	{
		_deprecationResolver = deprecationResolver;
	}

	/// <inheritdoc />
	public override string RuleId => "PKG-11";

	/// <inheritdoc />
	public override string RuleName => "Deprecated package repository is archived";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var earlyResult = PackagingCheckApplies(context, out var packableProjects);
		if (earlyResult is not null)
		{
			return earlyResult;
		}

		var deprecated = new List<PackageDeprecationStatus>();
		foreach (var packageId in ResolvePackageIds(context, packableProjects))
		{
			cancellationToken.ThrowIfCancellationRequested();

			var status = await _deprecationResolver(packageId, null, cancellationToken).ConfigureAwait(false);
			if (status is not null)
			{
				deprecated.Add(status);
			}
		}

		if (deprecated.Count == 0)
		{
			return Pass("No package published by this repository is deprecated on nuget.org.");
		}

		var ordered = deprecated
			.OrderBy(status => status.PackageId, StringComparer.OrdinalIgnoreCase)
			.ToList();

		return Fail(
			$"This repository still publishes deprecated package(s): {string.Join("; ", ordered.Select(Describe))}. "
				+ "A deprecated package's repository should be archived.",
			new RuleAdvisory
			{
				Summary = "Archive this repository — its package is deprecated on nuget.org.",
				Detail = $$"""
					{{string.Join("\n", ordered.Select(status => $"- `{status.PackageId}` is deprecated ({FormatReasons(status)}){FormatAlternate(status)}."))}}

					Deprecating the package and archiving the repository are two halves of one decision.
					This repository was assessed, which means it is not archived, so the second half was
					missed. Until it is archived it keeps generating governance remediations against code
					nobody should adopt.

					Before archiving, push any final commit — an archived repository is read-only. Confirm
					the README states the package is deprecated and names its replacement, close open pull
					requests, then archive the repository on GitHub.
					""",
				// Deliberately no remediation_type: archiving is an outward-facing act that ends a
				// repository's life, so it is described here and left for a human to carry out.
				Data = new()
				{
					["deprecated_packages"] = ordered.Select(status => status.PackageId).ToArray()
				}
			});
	}

	private static string Describe(PackageDeprecationStatus status)
		=> $"{status.PackageId} ({FormatReasons(status)})";

	private static string FormatReasons(PackageDeprecationStatus status)
		=> status.Reasons.Count > 0
			? string.Join(", ", status.Reasons)
			: "no reason given";

	private static string FormatAlternate(PackageDeprecationStatus status)
		=> status.AlternatePackageId is null
			? string.Empty
			: $", superseded by `{status.AlternatePackageId}`";

	/// <summary>
	/// The package identifiers this repository publishes: the declared PackageId where there is one,
	/// otherwise the project file name, which is what NuGet defaults the package identifier to.
	/// </summary>
	private static IEnumerable<string> ResolvePackageIds(RepositoryContext context, List<string> packableProjects)
		=> packableProjects
			.Select(projectPath =>
			{
				var declared = MsBuildProperties
					.TryGetValues(context.GetFileContent(projectPath), "PackageId")?
					.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

				return declared ?? Path.GetFileNameWithoutExtension(projectPath);
			})
			.Where(packageId => !string.IsNullOrWhiteSpace(packageId))
			.Distinct(StringComparer.OrdinalIgnoreCase);
}
