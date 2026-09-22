using System.Xml.Linq;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that code coverage is collected by <see cref="Standards.CodeCoveragePackage"/> rather than
/// by coverlet, which only functions as a VSTest data collector and so collects nothing under
/// Microsoft.Testing.Platform.
/// </summary>
/// <remarks>
/// Asked only of a repository whose tests actually run on Microsoft.Testing.Platform. On the others
/// the two packages swap roles and the rule inverts with them: coverlet is the collector that works,
/// and the Microsoft.Testing.Platform collector is the defect, because it selects a platform xunit v2
/// cannot serve.
/// </remarks>
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
		var testProjectContents = testProjects
			.Select(tp => (Project: tp, Content: context.GetFileContent(tp)))
			.ToList();

		// Only an xunit.v3 repository runs on Microsoft.Testing.Platform, and on the others this
		// collector is not merely useless. It depends on Microsoft.Testing.Platform, so referencing it
		// is enough to put the platform in the dependency graph; the SDK then selects it, xunit v2 has
		// no entry point for it, and no test is discovered at all. TST-06 guards the same mistake in
		// global.json. Nothing else catches this one: the project still builds with no errors and no
		// warnings, so only a test count would show it.
		if (!UsesMicrosoftTestingPlatform(context))
		{
			return Task.FromResult(EvaluateVsTestRepository(usesCpm, dirPackages, testProjectContents));
		}

		var pinnedInProps = PinsPackageVersion(dirPackages, Standards.CodeCoveragePackage);

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

	/// <summary>
	/// Evaluates a repository whose tests still run on VSTest, where the two packages swap roles:
	/// <see cref="Standards.VsTestCodeCoveragePackage"/> is the collector that works and
	/// <see cref="Standards.CodeCoveragePackage"/> is the defect.
	/// </summary>
	/// <remarks>
	/// Inverted rather than skipped, for the same reason TST-06 removes a stranded test.runner: a
	/// governance remediation put this package here, so only a remediation takes it back out.
	/// </remarks>
	/// <param name="usesCpm">Whether the repository manages package versions centrally.</param>
	/// <param name="dirPackages">The content of Directory.Packages.props, if any.</param>
	/// <param name="testProjectContents">Each test project and its content.</param>
	/// <returns>The rule result for a VSTest repository.</returns>
	private RuleResult EvaluateVsTestRepository(
		bool usesCpm,
		string? dirPackages,
		List<(string Project, string? Content)> testProjectContents)
	{
		var mtpPinnedInProps = PinsPackageVersion(dirPackages, Standards.CodeCoveragePackage);
		var projectsWithMtpCollector = testProjectContents
			.Where(tp => ReferencesPackageDirectly(tp.Content, Standards.CodeCoveragePackage))
			.Select(tp => tp.Project)
			.ToArray();

		if (!mtpPinnedInProps && projectsWithMtpCollector.Length == 0)
		{
			// Nothing to ask for until TST-02 moves the repository to xunit.v3. Asking for the
			// Microsoft.Testing.Platform collector here is what stopped test discovery on the
			// repositories that took the advice.
			return NotApplicable(
				$"No xunit.v3 reference found; {Standards.VsTestCodeCoveragePackage} is the collector that works under VSTest, so {Standards.CodeCoveragePackage} does not apply.");
		}

		return Broken(
			$"{Standards.CodeCoveragePackage} is referenced, but this repository's tests do not run on Microsoft.Testing.Platform. It brings Microsoft.Testing.Platform into the dependency graph, which xunit v2 cannot serve, so no test can be discovered.",
			new RuleAdvisory
			{
				Summary = $"Remove {Standards.CodeCoveragePackage} and restore {Standards.VsTestCodeCoveragePackage}, or migrate the tests to xunit.v3.",
				Detail = $$"""
					`{{Standards.CodeCoveragePackage}}` depends on `Microsoft.Testing.Platform`, so
					referencing it is enough to put the platform in the dependency graph. The SDK then
					selects it, xunit v2 has no entry point for it, and nothing is discoverable:

					```
					Not all tests from the test run selection could be discovered.
					Make sure to build your test project.
					```

					Nothing else catches this. The test project still builds with 0 errors and 0 warnings,
					and a CI job that does not assert a non-zero test count reports success.

					Removing the `test.runner` key from global.json is not enough on its own, which is all
					TST-06 can do: while this package remains it is still the only thing putting
					`Microsoft.Testing.Platform` into `project.assets.json`, so the platform is still
					selected.

					On a repository still on VSTest, `{{Standards.VsTestCodeCoveragePackage}}` is the
					collector that works. Take this package out and put that one back, or migrate the test
					project to `xunit.v3` (TST-02), after which this package becomes required rather than
					harmful.
					""",
				Data = new()
				{
					// Deliberately the same remediation type as the forward case. That remediation is
					// already symmetric, "ensure this package, remove these dead ones", so inverting the
					// roles in the payload inverts the fix with no new type to register.
					["remediation_type"] = "ensure_code_coverage_setup",
					["package_name"] = Standards.VsTestCodeCoveragePackage,
					["package_version"] = Standards.VsTestCodeCoverageVersion,
					["uses_cpm"] = usesCpm,
					["pinned_in_props"] = PinsPackageVersion(dirPackages, Standards.VsTestCodeCoveragePackage),
					["referenced_in_test_project"] = testProjectContents
						.Any(tp => ReferencesPackageDirectly(tp.Content, Standards.VsTestCodeCoveragePackage)),
					["dead_packages"] = new[] { Standards.CodeCoveragePackage },
					["projects"] = projectsWithMtpCollector,
					// The project the collector came out of, so coverlet goes back where it was taken from.
					["target_project"] = projectsWithMtpCollector.FirstOrDefault()
						?? testProjectContents[0].Project
				}
			});
	}

	/// <summary>
	/// A failing result carrying <see cref="AssessmentSeverity.Error"/> rather than the rule's declared
	/// <see cref="Severity"/>.
	/// </summary>
	/// <remarks>
	/// The rule is a warning because inert coverage configuration collects nothing while everything
	/// else keeps working. The VSTest case is not that: no test runs at all, which is what TST-06 calls
	/// an error for the same cause.
	/// </remarks>
	/// <param name="message">The failure message.</param>
	/// <param name="advisory">Structured advisory for remediation.</param>
	/// <returns>A failing result at error severity.</returns>
	private RuleResult Broken(string message, RuleAdvisory advisory) => new()
	{
		RuleId = RuleId,
		RuleName = RuleName,
		Category = Category,
		Severity = AssessmentSeverity.Error,
		Passed = false,
		Message = message,
		Advisory = advisory
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
