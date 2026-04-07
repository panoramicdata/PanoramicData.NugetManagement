using System.Xml.Linq;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that coverlet.collector is referenced for code coverage.
/// </summary>
public class CoverletCollectorRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TST-04";

	/// <inheritdoc />
	public override string RuleName => "coverlet.collector referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		var usesCpm = UsesCentralPackageManagement(dirPackages);
		var pinnedInProps = HasPackageVersion(dirPackages, "coverlet.collector");

		var testProjects = context.FindFiles(".csproj")
			.Where(f => f.Contains(".Test", StringComparison.OrdinalIgnoreCase))
			.ToList();

		var referencedInTestProject = testProjects.Any(tp => HasPackageReference(context.GetFileContent(tp), "coverlet.collector"));

		if (usesCpm)
		{
			return Task.FromResult(pinnedInProps && referencedInTestProject
				? Pass("coverlet.collector is pinned in Directory.Packages.props and referenced by a test project.")
				: Fail(
					CreateFailureMessage(usesCpm, pinnedInProps, referencedInTestProject),
					CreateAdvisory(testProjects, usesCpm, pinnedInProps, referencedInTestProject)));
		}

		return Task.FromResult(referencedInTestProject
			? Pass("coverlet.collector is referenced by a test project.")
			: Fail(
				CreateFailureMessage(usesCpm, pinnedInProps, referencedInTestProject),
				CreateAdvisory(testProjects, usesCpm, pinnedInProps, referencedInTestProject)));
	}

	private static string CreateFailureMessage(bool usesCpm, bool pinnedInProps, bool referencedInTestProject)
		=> usesCpm
			? pinnedInProps
				? "coverlet.collector is pinned in Directory.Packages.props but not referenced by any test project."
				: referencedInTestProject
					? "coverlet.collector is referenced by a test project but is not pinned in Directory.Packages.props."
					: "coverlet.collector is not pinned in Directory.Packages.props or referenced by any test project."
			: "coverlet.collector is not referenced by any test project.";

	private static RuleAdvisory CreateAdvisory(List<string> testProjects, bool usesCpm, bool pinnedInProps, bool referencedInTestProject)
		=> new()
		{
			Summary = usesCpm
				? "When central package management is enabled, pin coverlet.collector in Directory.Packages.props and reference it from a test project."
				: "Add coverlet.collector to a test project for code coverage collection.",
			Detail = usesCpm
				? "With central package management enabled, `coverlet.collector` must appear as a `<PackageVersion>` in `Directory.Packages.props` and as a `<PackageReference Include=\"coverlet.collector\" />` in at least one test project so coverage can actually be collected."
				: "No test project references `coverlet.collector`. Add `<PackageReference Include=\"coverlet.collector\" />` to at least one test project so code coverage can be collected.",
			Data = new()
			{
				["remediation_type"] = "ensure_coverlet_collector_setup",
				["package_name"] = "coverlet.collector",
				["package_version"] = "8.0.1",
				["uses_cpm"] = usesCpm,
				["pinned_in_props"] = pinnedInProps,
				["referenced_in_test_project"] = referencedInTestProject,
				["target_project"] = testProjects.FirstOrDefault() ?? string.Empty
			}
		};

	private static bool UsesCentralPackageManagement(string? dirPackages)
		=> TryParse(dirPackages, out var doc)
			&& string.Equals(doc.Descendants("ManagePackageVersionsCentrally").FirstOrDefault()?.Value, "true", StringComparison.OrdinalIgnoreCase);

	private static bool HasPackageVersion(string? content, string packageId)
		=> TryParse(content, out var doc)
			&& doc.Descendants("PackageVersion").Any(element => MatchesPackage(element, packageId));

	private static bool HasPackageReference(string? content, string packageId)
		=> TryParse(content, out var doc)
			&& doc.Descendants("PackageReference").Any(element => MatchesPackage(element, packageId));

	private static bool MatchesPackage(XElement element, string packageId)
		=> string.Equals(element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value, packageId, StringComparison.OrdinalIgnoreCase);

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
