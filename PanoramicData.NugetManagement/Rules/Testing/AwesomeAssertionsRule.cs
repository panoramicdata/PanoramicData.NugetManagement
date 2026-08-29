using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that AwesomeAssertions is used rather than FluentAssertions.
/// </summary>
/// <remarks>
/// FluentAssertions 8 requires a paid Xceed licence for commercial use. AwesomeAssertions is the
/// API-compatible fork that does not, which is why this is an Error rather than a preference: a
/// repository that keeps FluentAssertions is either paying for it or is out of compliance with its
/// licence.
/// </remarks>
public class AwesomeAssertionsRule : RuleBase
{
	/// <summary>The packages this rule bans, and what each becomes.</summary>
	/// <remarks>
	/// Order matters. The Analyzers package has to be rewritten before the bare name: rewrite the bare
	/// name first and "FluentAssertions.Analyzers" has already become "AwesomeAssertions.Analyzers",
	/// so the Analyzers rule no longer matches and its version pin is left behind.
	/// </remarks>
	private static readonly (string Pattern, string Replacement)[] _replacements =
	[
		("FluentAssertions.Analyzers", "AwesomeAssertions.Analyzers"),
		("FluentAssertions", "AwesomeAssertions")
	];

	/// <inheritdoc />
	public override string RuleId => "TST-08";

	/// <inheritdoc />
	public override string RuleName => "AwesomeAssertions used, not FluentAssertions";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var testProjects = context.FindTestProjectFiles().ToList();
		if (testProjects.Count == 0)
		{
			return Task.FromResult(NotApplicable("No test projects found; rule does not apply."));
		}

		// includeVariants catches FluentAssertions.Analyzers as well as the library itself: the
		// analyzer package alone still pulls the licence question along with it.
		var pinnedCentrally = ReferencesPackage(
			context.GetFileContent("Directory.Packages.props"),
			"FluentAssertions",
			includeVariants: true);

		var referencingProjects = testProjects
			.Where(project => ReferencesPackageDirectly(context.GetFileContent(project), "FluentAssertions", includeVariants: true))
			.ToArray();

		if (!pinnedCentrally && referencingProjects.Length == 0)
		{
			return Task.FromResult(Pass("FluentAssertions is not referenced."));
		}

		var where = pinnedCentrally
			? referencingProjects.Length > 0
				? $"Directory.Packages.props and {string.Join(", ", referencingProjects)}"
				: "Directory.Packages.props"
			: string.Join(", ", referencingProjects);

		return Task.FromResult(Fail(
			$"FluentAssertions is referenced in {where}; use AwesomeAssertions instead.",
			new RuleAdvisory
			{
				Summary = "Replace FluentAssertions with AwesomeAssertions.",
				Detail = """
					FluentAssertions 8 requires a paid Xceed licence for commercial use.
					`AwesomeAssertions` is the API-compatible fork that does not, so the swap is a rename
					rather than a migration.

					Three places have to change together, which is why the remediation sweeps all of them:

					- `Directory.Packages.props`, for the version pin.
					- Every `.csproj`, for the package reference.
					- Every `.cs` file, for `using FluentAssertions;` — the packages can be swapped and
					  nothing will compile until the using directives follow.

					`FluentAssertions.Analyzers` is renamed first. Rewriting the bare name first would turn
					it into `AwesomeAssertions.Analyzers` before its own rule ran, leaving its version pin
					pointing at a package that no longer exists.
					""",
				Data = new()
				{
					["remediation_type"] = "replace_regex_in_files",
					["globs"] = new[] { "Directory.Packages.props", "**/*.csproj", "**/*.cs" },
					["patterns"] = _replacements.Select(replacement => replacement.Pattern).ToArray(),
					["replacements"] = _replacements.Select(replacement => replacement.Replacement).ToArray(),
					["pinned_centrally"] = pinnedCentrally,
					["projects"] = referencingProjects
				}
			}));
	}
}
