using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that Centralized Package Management is enabled.
/// </summary>
public class CpmEnabledRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CPM-01";

	/// <inheritdoc />
	public override string RuleName => "CPM enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CentralPackageManagement;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("Directory.Packages.props");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"Directory.Packages.props not found.",
				"Create Directory.Packages.props with <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>."));
		}

		return Task.FromResult(Contains(content, "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>")
			? Pass("Centralized Package Management is enabled.")
			: Fail(
				"Directory.Packages.props does not enable ManagePackageVersionsCentrally.",
				"Add <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally> to Directory.Packages.props."));
	}
}

/// <summary>
/// Checks that .csproj files do not have Version= attributes on PackageReference elements.
/// </summary>
public partial class CpmNoVersionInCsprojRule : RuleBase
{
	[GeneratedRegex(@"<PackageReference\s+[^>]*Version\s*=", RegexOptions.IgnoreCase)]
	private static partial Regex PackageReferenceVersionPattern();
	/// <inheritdoc />
	public override string RuleId => "CPM-02";

	/// <inheritdoc />
	public override string RuleName => "No Version in .csproj PackageReferences";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CentralPackageManagement;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var csprojFiles = context.FindFiles(".csproj").ToList();
		var violations = new List<string>();

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is null)
			{
				continue;
			}

			// Check for PackageReference with Version= attribute (but not PackageVersion which is correct)
			if (PackageReferenceVersionPattern().IsMatch(content))
			{
				violations.Add(csproj);
			}
		}

		return Task.FromResult(violations.Count == 0
			? Pass("No .csproj files have Version= on PackageReference elements.")
			: Fail(
				$"The following .csproj files have Version= on PackageReference elements: {string.Join(", ", violations)}",
				"Remove Version attributes from PackageReference elements; versions should be in Directory.Packages.props."));
	}
}
