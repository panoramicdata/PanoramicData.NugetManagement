using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that .csproj files do not have Version= attributes on PackageReference elements.
/// <para>
/// Projects that opt out of central package management are excluded. CPM is resolved per
/// directory, not per repository: a project whose nearest <c>Directory.Packages.props</c> sets
/// <c>ManagePackageVersionsCentrally</c> to <c>false</c> gets its versions from the inline
/// attributes and nowhere else, so removing them leaves it unable to restore (NU1015).
/// </para>
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
			if (!PackageReferenceVersionPattern().IsMatch(content))
			{
				continue;
			}

			// An inline version is only a violation where CPM actually governs the project. Under an
			// opt-out it is the sole source of the version, and removing it breaks restore.
			if (OptsOutOfCentralPackageManagement(context, csproj))
			{
				continue;
			}

			violations.Add(csproj);
		}

		return Task.FromResult(violations.Count == 0
			? Pass("No .csproj files have Version= on PackageReference elements.")
			: Fail(
				$"The following .csproj files have Version= on PackageReference elements: {string.Join(", ", violations)}",
				new RuleAdvisory
				{
					Summary = "Remove `Version` attributes from PackageReference elements; move versions to Directory.Packages.props",
					Detail = "Remove all `Version` attributes from `<PackageReference>` elements in the listed .csproj files. Versions should be managed centrally in `Directory.Packages.props`.",
					Data = new()
					{
						["remediation_type"] = "remove_packagereference_versions",
						["projects"] = violations.ToArray()
					}
				}));
	}

	/// <summary>
	/// Whether a project sits under an explicit central package management opt-out, resolved the way
	/// MSBuild resolves the property: the nearest <c>Directory.Packages.props</c> at or above the
	/// project's own directory decides, and nearer wins.
	/// </summary>
	/// <remarks>
	/// Only an explicit <c>false</c> exempts a project. A repository with no
	/// <c>Directory.Packages.props</c> at all is left to CPM-01, which is the rule that asks for one.
	/// </remarks>
	private static bool OptsOutOfCentralPackageManagement(RepositoryContext context, string csprojPath)
	{
		var directory = csprojPath.Replace(@"\", "/");
		var lastSeparator = directory.LastIndexOf('/');
		directory = lastSeparator < 0 ? string.Empty : directory[..lastSeparator];

		while (true)
		{
			var candidate = directory.Length == 0
				? "Directory.Packages.props"
				: $"{directory}/Directory.Packages.props";

			var props = context.GetFileContent(candidate);
			if (props is not null)
			{
				return HasMsBuildProperty(props, "ManagePackageVersionsCentrally", "false");
			}

			if (directory.Length == 0)
			{
				return false;
			}

			var separator = directory.LastIndexOf('/');
			directory = separator < 0 ? string.Empty : directory[..separator];
		}
	}
}
