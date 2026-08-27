using System.Xml.Linq;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that code coverage is collected by <see cref="Standards.CodeCoveragePackage"/> rather than
/// by coverlet, which only functions as a VSTest data collector and so collects nothing under
/// Microsoft.Testing.Platform.
/// </summary>
public class CodeCoverageCollectorRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TST-04";

	/// <inheritdoc />
	public override string RuleName => "Microsoft.Testing.Extensions.CodeCoverage referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var testProjects = context.FindTestProjectFiles().ToList();
		if (testProjects.Count == 0)
		{
			return Task.FromResult(NotApplicable("No test projects found; rule does not apply."));
		}

		var dirPackages = context.GetFileContent("Directory.Packages.props");
		var usesCpm = UsesCentralPackageManagement(dirPackages);
		var pinnedInProps = PinsPackageVersion(dirPackages, Standards.CodeCoveragePackage);
		var testProjectContents = testProjects
			.Select(tp => (Project: tp, Content: context.GetFileContent(tp)))
			.ToList();

		var referencedInTestProject = testProjectContents
			.Any(tp => ReferencesPackageDirectly(tp.Content, Standards.CodeCoveragePackage));

		// coverlet.collector and coverlet.msbuild both hook the VSTest target, which no longer runs.
		// They fail quietly — "Zero tests ran", exit code 5 — so their presence reads as working
		// coverage configuration while nothing is being collected.
		var deadPackages = Standards.DeadCoverletPackages
			.Where(p => PinsPackageVersion(dirPackages, p)
				|| testProjectContents.Any(tp => ReferencesPackageDirectly(tp.Content, p)))
			.ToArray();

		var collectorMissing = usesCpm
			? !pinnedInProps || !referencedInTestProject
			: !referencedInTestProject;

		if (!collectorMissing && deadPackages.Length == 0)
		{
			return Task.FromResult(Pass(usesCpm
				? $"{Standards.CodeCoveragePackage} is pinned in Directory.Packages.props and referenced by a test project."
				: $"{Standards.CodeCoveragePackage} is referenced by a test project."));
		}

		var projectsWithDeadPackages = testProjectContents
			.Where(tp => Standards.DeadCoverletPackages.Any(p => ReferencesPackageDirectly(tp.Content, p)))
			.Select(tp => tp.Project)
			.ToArray();

		return Task.FromResult(Fail(
			CreateFailureMessage(usesCpm, pinnedInProps, referencedInTestProject, collectorMissing, deadPackages),
			CreateAdvisory(testProjects, usesCpm, pinnedInProps, referencedInTestProject, deadPackages, projectsWithDeadPackages)));
	}

	private static string CreateFailureMessage(
		bool usesCpm,
		bool pinnedInProps,
		bool referencedInTestProject,
		bool collectorMissing,
		string[] deadPackages)
	{
		var parts = new List<string>();

		if (collectorMissing)
		{
			parts.Add(usesCpm
				? pinnedInProps
					? $"{Standards.CodeCoveragePackage} is pinned in Directory.Packages.props but not referenced by any test project."
					: referencedInTestProject
						? $"{Standards.CodeCoveragePackage} is referenced by a test project but is not pinned in Directory.Packages.props."
						: $"{Standards.CodeCoveragePackage} is not pinned in Directory.Packages.props or referenced by any test project."
				: $"{Standards.CodeCoveragePackage} is not referenced by any test project.");
		}

		if (deadPackages.Length > 0)
		{
			parts.Add($"{string.Join(" and ", deadPackages)} {(deadPackages.Length == 1 ? "collects" : "collect")} nothing under Microsoft.Testing.Platform and should be removed.");
		}

		return string.Join(" ", parts);
	}

	private static RuleAdvisory CreateAdvisory(
		List<string> testProjects,
		bool usesCpm,
		bool pinnedInProps,
		bool referencedInTestProject,
		string[] deadPackages,
		string[] projectsWithDeadPackages)
		=> new()
		{
			Summary = deadPackages.Length > 0
				? $"Replace {string.Join(" and ", deadPackages)} with {Standards.CodeCoveragePackage}, which works under Microsoft.Testing.Platform."
				: usesCpm
					? $"Pin {Standards.CodeCoveragePackage} in Directory.Packages.props and reference it from a test project."
					: $"Add {Standards.CodeCoveragePackage} to a test project so code coverage can be collected.",
			Detail = $$"""
				`coverlet.collector` is a VSTest data collector, and `coverlet.msbuild` hooks the VSTest
				target. Neither runs under Microsoft.Testing.Platform, which is what `dotnet test` uses
				on the .NET 10 SDK (see TST-06), so any coverlet configuration is inert. The failure is
				quiet: `--collect:"XPlat Code Coverage"` does not error, it reports `Zero tests ran` with
				exit code 5, which reads like a test filter problem rather than a dead collector. A
				repository can look configured for coverage while collecting none of it.

				Pin `{{Standards.CodeCoveragePackage}}` at `{{Standards.CodeCoverageVersion}}`, reference
				it from each test project, and collect coverage with:

				```
				dotnet test --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
				```

				Settings move from a coverlet `.runsettings.json` to the Microsoft code coverage XML
				format, passed with `--coverage-settings`:

				| coverlet | Microsoft code coverage |
				|---|---|
				| `Include` / `Exclude` (`[Assembly]*`) | `ModulePaths` → `Include` / `Exclude` (regex on module path) |
				| `ExcludeByAttribute` | `Attributes` → `Exclude` (fully-qualified attribute regex) |
				| `ExcludeByFile` | `Sources` → `Exclude` |
				| `Format: cobertura` | `--coverage-output-format cobertura` |

				Port the attribute exclusions rather than treating them as optional: the Microsoft
				collector includes generated code by default, where coverlet excluded it via
				`ExcludeByAttribute`. On the repository this guidance came from, coverage read 82.9%
				before the exclusions were ported and 86.9% after, so a naive migration silently changes
				the reported figure.
				""",
			Data = new()
			{
				["remediation_type"] = "ensure_code_coverage_setup",
				["package_name"] = Standards.CodeCoveragePackage,
				["package_version"] = Standards.CodeCoverageVersion,
				["uses_cpm"] = usesCpm,
				["pinned_in_props"] = pinnedInProps,
				["referenced_in_test_project"] = referencedInTestProject,
				["dead_packages"] = deadPackages,
				["projects"] = projectsWithDeadPackages,
				["target_project"] = testProjects.FirstOrDefault() ?? string.Empty
			}
		};

	private static bool UsesCentralPackageManagement(string? dirPackages)
		=> TryParse(dirPackages, out var doc)
			&& string.Equals(doc.Descendants("ManagePackageVersionsCentrally").FirstOrDefault()?.Value, "true", StringComparison.OrdinalIgnoreCase);

	private static bool TryParse(string? content, out XDocument document)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			document = null!;
			return false;
		}

		try
		{
			document = XDocument.Parse(content);
			return true;
		}
		catch
		{
			document = null!;
			return false;
		}
	}
}
