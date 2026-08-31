using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that no repository still depends on FluentAssertions, which the estate has replaced with
/// AwesomeAssertions.
/// </summary>
/// <remarks>
/// <para>
/// FluentAssertions 8 introduced a paid licence for commercial use. AwesomeAssertions is the
/// community fork of the last freely-licensed release, with the same API and the same namespace
/// layout, so it is a flat replacement rather than a migration to a different library.
/// </para>
/// <para>
/// This exists as a rule of its own rather than being left to the version rules because a version
/// bump is the wrong answer here and an actively harmful one: carrying a repository from
/// FluentAssertions 6 to 8 is what moves it onto the licensed release, in the opposite direction from
/// the rest of the estate. Deliberately not an <see cref="IGovernsDependency"/> rule for the same
/// reason — it does not enforce a minimum version of FluentAssertions, it removes the dependency.
/// </para>
/// <para>
/// No remediation payload, because the swap has to hold the package identity and the version together:
/// the AwesomeAssertions version line does not continue FluentAssertions' own, so a rename that kept
/// the version would reference a package that does not exist. <c>FluentAssertionsMigrationPlaybook</c>
/// is what carries it, and the estate floor is what supplies the version.
/// </para>
/// </remarks>
public class FluentAssertionsMigrationRule : RuleBase
{
	/// <summary>
	/// The packages that move, old name to new. The analyzers package forked alongside the main one.
	/// </summary>
	private static readonly (string From, string To)[] _replacements =
	[
		("FluentAssertions.Analyzers", "AwesomeAssertions.Analyzers"),
		("FluentAssertions", "AwesomeAssertions")
	];

	/// <inheritdoc />
	public override string RuleId => "PKG-13";

	/// <inheritdoc />
	public override string RuleName => "FluentAssertions replaced by AwesomeAssertions";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	/// <remarks>
	/// A warning rather than an error: the repository builds and its tests pass, and nothing is broken
	/// until somebody looks at the licence. It is the estate being inconsistent about which assertion
	/// library it uses, which is exactly what a warning is for.
	/// </remarks>
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var references = PackageReferenceScanner.Scan(context);
		if (references.Count == 0)
		{
			return Task.FromResult(Pass("No explicit NuGet package versions were found to evaluate."));
		}

		var found = references
			.Where(reference => _replacements.Any(replacement => string.Equals(
				reference.PackageId, replacement.From, StringComparison.OrdinalIgnoreCase)))
			.OrderBy(reference => reference.PackageId, StringComparer.OrdinalIgnoreCase)
			.ThenBy(reference => reference.FilePath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (found.Count == 0)
		{
			return Task.FromResult(Pass("No FluentAssertions dependency is declared."));
		}

		return Task.FromResult(Fail(
			"The following FluentAssertions dependencies should be AwesomeAssertions: "
				+ string.Join("; ", found.Select(r => $"{r.PackageId} {r.CurrentVersion} ({r.FilePath})"))
				+ ".",
			new RuleAdvisory
			{
				Summary = "Migrate from FluentAssertions to AwesomeAssertions.",
				Detail = $$"""
					{{string.Join("\n", found.Select(r => $"- `{r.PackageId}` {r.CurrentVersion} in `{r.FilePath}` → `{Replacement(r.PackageId)}`"))}}

					FluentAssertions 8 introduced a paid licence for commercial use. AwesomeAssertions is
					the community fork of the last freely-licensed release: same API, same namespaces, so
					the change is a rename plus a version, not a rewrite of the assertions.

					Do not let a version rule carry this forward instead. Bumping FluentAssertions is what
					moves the repository onto the licensed release, away from where the rest of the estate
					has gone.

					Not applied automatically, because the package identity and the version have to change
					together: AwesomeAssertions does not continue FluentAssertions' version line, so a
					rename that kept the old version would reference a package that does not exist.
					""",
				Data = new()
				{
					["fluent_assertions_references"] = found
						.Select(r => string.Join('|', r.FilePath, r.PackageId, Replacement(r.PackageId)))
						.ToArray()
				}
			}));
	}

	/// <summary>
	/// The package that replaces one this rule found.
	/// </summary>
	private static string Replacement(string packageId)
		=> _replacements
			.First(replacement => string.Equals(
				packageId, replacement.From, StringComparison.OrdinalIgnoreCase))
			.To;
}
