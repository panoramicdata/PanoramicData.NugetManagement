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
					pinnedInProps
						? "coverlet.collector is pinned in Directory.Packages.props but not referenced by any test project."
						: referencedInTestProject
							? "coverlet.collector is referenced by a test project but is not pinned in Directory.Packages.props."
							: "coverlet.collector is not pinned in Directory.Packages.props or referenced by any test project.",
					new RuleAdvisory
					{
						Summary = "When central package management is enabled, pin coverlet.collector in Directory.Packages.props and reference it from a test project.",
						Detail = "With central package management enabled, `coverlet.collector` must appear as a `<PackageVersion>` in `Directory.Packages.props` and as a `<PackageReference Include=\"coverlet.collector\" />` in at least one test project so coverage can actually be collected."
					}));
		}

		return Task.FromResult(referencedInTestProject
			? Pass("coverlet.collector is referenced by a test project.")
			: Fail(
				"coverlet.collector is not referenced by any test project.",
				new RuleAdvisory
				{
					Summary = "Add coverlet.collector to a test project for code coverage collection.",
					Detail = "No test project references `coverlet.collector`. Add `<PackageReference Include=\"coverlet.collector\" />` to at least one test project so code coverage can be collected."
				}));
	}

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
