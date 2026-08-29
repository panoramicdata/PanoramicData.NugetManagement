using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a repository does not depend on packages their authors have deprecated.
/// </summary>
/// <remarks>
/// A deprecated dependency is a warning rather than an error because the fix is rarely mechanical:
/// the suggested alternative is a different package with a different API, so replacing it is a code
/// change requiring judgement. This rule therefore reports and explains, and deliberately emits no
/// remediation payload — an automated swap would break the build and be rolled back.
/// </remarks>
public class NoDeprecatedDependenciesRule : RuleBase
{
	private readonly Func<string, string?, CancellationToken, Task<PackageDeprecationStatus?>> _deprecationResolver;

	/// <summary>
	/// Initializes a new instance of the <see cref="NoDeprecatedDependenciesRule"/> class.
	/// </summary>
	public NoDeprecatedDependenciesRule()
		: this(new NuGetDeprecationChecker().GetDeprecationAsync)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="NoDeprecatedDependenciesRule"/> class.
	/// </summary>
	/// <param name="deprecationResolver">Resolves the deprecation status of a package version.</param>
	public NoDeprecatedDependenciesRule(
		Func<string, string?, CancellationToken, Task<PackageDeprecationStatus?>> deprecationResolver)
	{
		_deprecationResolver = deprecationResolver;
	}

	/// <inheritdoc />
	public override string RuleId => "PKG-12";

	/// <inheritdoc />
	public override string RuleName => "No deprecated package dependencies";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var references = PackageReferenceScanner.Scan(context);
		if (references.Count == 0)
		{
			return Pass("No explicit NuGet package versions were found to evaluate.");
		}

		var findings = new List<DeprecatedDependency>();
		var checkedPackages = new Dictionary<string, PackageDeprecationStatus?>(StringComparer.OrdinalIgnoreCase);

		foreach (var reference in references)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var key = $"{reference.PackageId}/{reference.CurrentVersion}";
			if (!checkedPackages.TryGetValue(key, out var status))
			{
				status = await _deprecationResolver(reference.PackageId, reference.CurrentVersion, cancellationToken)
					.ConfigureAwait(false);
				checkedPackages[key] = status;
			}

			if (status is not null)
			{
				findings.Add(new DeprecatedDependency(reference.FilePath, status));
			}
		}

		if (findings.Count == 0)
		{
			return Pass("No referenced NuGet package is deprecated.");
		}

		var ordered = findings
			.OrderBy(finding => finding.Status.PackageId, StringComparer.OrdinalIgnoreCase)
			.ThenBy(finding => finding.FilePath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		return Fail(
			$"The following referenced NuGet packages are deprecated: {string.Join("; ", ordered.Select(Describe))}.",
			new RuleAdvisory
			{
				Summary = "Replace deprecated package dependencies with their supported alternatives.",
				Detail = $$"""
					{{string.Join("\n", ordered.Select(finding => $"- `{finding.Status.PackageId}` in `{finding.FilePath}` is deprecated ({FormatReasons(finding.Status)}){FormatAlternate(finding.Status)}.{FormatMessage(finding.Status)}"))}}

					The package author has marked these as deprecated, so they receive no further fixes.
					Migrate to the suggested alternative where one is named; where none is, find a supported
					replacement or vendor the functionality.

					This is not applied automatically: an alternative package is a different library with a
					different API, so the swap is a code change that needs review and testing.
					""",
				Data = new()
				{
					["deprecated_dependencies"] = ordered
						.Select(finding => string.Join(
							'|',
							finding.FilePath,
							finding.Status.PackageId,
							finding.Status.AlternatePackageId ?? string.Empty))
						.ToArray()
				}
			});
	}

	private static string Describe(DeprecatedDependency finding)
		=> finding.Status.AlternatePackageId is null
			? $"{finding.Status.PackageId} ({finding.FilePath})"
			: $"{finding.Status.PackageId} → {finding.Status.AlternatePackageId} ({finding.FilePath})";

	private static string FormatReasons(PackageDeprecationStatus status)
		=> status.Reasons.Count > 0
			? string.Join(", ", status.Reasons)
			: "no reason given";

	private static string FormatAlternate(PackageDeprecationStatus status)
		=> status.AlternatePackageId is null
			? string.Empty
			: $", superseded by `{status.AlternatePackageId}`";

	private static string FormatMessage(PackageDeprecationStatus status)
		=> status.Message is null
			? string.Empty
			: $" The author says: \"{status.Message}\"";

	private sealed record DeprecatedDependency(string FilePath, PackageDeprecationStatus Status);
}
