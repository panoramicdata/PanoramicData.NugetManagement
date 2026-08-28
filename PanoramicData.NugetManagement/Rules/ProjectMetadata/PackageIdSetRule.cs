using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageId is set in packable projects.
/// </summary>
public class PackageIdSetRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-01";

	/// <inheritdoc />
	public override string RuleName => "PackageId set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ProjectMetadata;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var notApplicable = PackagingCheckApplies(context, out var projects);
		if (notApplicable is not null)
		{
			return Task.FromResult(notApplicable);
		}

		var missing = projects
			.Where(csproj => !HasMsBuildProperty(context.GetFileContent(csproj), "PackageId"))
			.ToList();

		if (missing.Count > 0)
		{
			return Task.FromResult(Fail(
				$"{string.Join(", ", missing)} do(es) not have PackageId set.",
				new RuleAdvisory
				{
					Summary = "Add <PackageId>YourPackageId</PackageId> to the .csproj.",
					Detail = $"These published projects do not have `<PackageId>` set: {string.Join(", ", missing)}. Add `<PackageId>YourPackageId</PackageId>` to a `<PropertyGroup>`.",
					Data = new()
					{
						["file"] = missing[0],
						["projects_missing"] = missing.ToArray(),
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "PackageId",
						["property_value"] = Path.GetFileNameWithoutExtension(missing[0])
					}
				}));
		}

		return Task.FromResult(Pass($"All {projects.Count} published project(s) have PackageId set."));
	}
}