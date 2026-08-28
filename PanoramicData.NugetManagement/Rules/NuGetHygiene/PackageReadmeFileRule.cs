using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageReadmeFile is set.
/// </summary>
public class PackageReadmeFileRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-03";

	/// <inheritdoc />
	public override string RuleName => "PackageReadmeFile set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var notApplicable = PackagingCheckApplies(context, out var projects);
		if (notApplicable is not null)
		{
			return Task.FromResult(notApplicable);
		}

		var missing = projects
			.Where(csproj => !HasMsBuildProperty(context.GetFileContent(csproj), "PackageReadmeFile"))
			.ToList();

		if (missing.Count > 0)
		{
			return Task.FromResult(Fail(
				$"{string.Join(", ", missing)} do(es) not set PackageReadmeFile.",
				new RuleAdvisory
				{
					Summary = "Add <PackageReadmeFile>README.md</PackageReadmeFile> and pack the README.md via <None Include>.",
					Detail = $"These published projects do not set `PackageReadmeFile`: {string.Join(", ", missing)}. Add `<PackageReadmeFile>README.md</PackageReadmeFile>` to a `<PropertyGroup>` and include `<None Include=\"..\\README.md\" Pack=\"true\" PackagePath=\"\\\"/>` in an `<ItemGroup>`.",
					Data = new()
					{
						["projects"] = missing.ToArray(),
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "PackageReadmeFile",
						["property_value"] = "README.md"
					}
				}));
		}

		return Task.FromResult(Pass($"All {projects.Count} published project(s) have PackageReadmeFile set."));
	}
}
