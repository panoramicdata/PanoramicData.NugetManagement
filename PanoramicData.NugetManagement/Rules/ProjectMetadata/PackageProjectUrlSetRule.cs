using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageProjectUrl is set in packable projects.
/// </summary>
public class PackageProjectUrlSetRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-04";

	/// <inheritdoc />
	public override string RuleName => "PackageProjectUrl set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ProjectMetadata;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var notApplicable = PackagingCheckApplies(context, out var projects);
		if (notApplicable is not null)
		{
			return Task.FromResult(notApplicable);
		}

		var missing = projects
			.Where(csproj => !HasMsBuildProperty(context.GetFileContent(csproj), "PackageProjectUrl"))
			.ToList();

		if (missing.Count > 0)
		{
			return Task.FromResult(Fail(
				$"{string.Join(", ", missing)} do(es) not have PackageProjectUrl set.",
				new RuleAdvisory
				{
					Summary = "Add <PackageProjectUrl>https://github.com/org/repo</PackageProjectUrl> to the .csproj.",
					Detail = $"These published projects do not have `<PackageProjectUrl>` set: {string.Join(", ", missing)}. Add `<PackageProjectUrl>https://github.com/org/repo</PackageProjectUrl>` to a `<PropertyGroup>`.",
					Data = new()
					{
						["projects"] = missing.ToArray(),
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "PackageProjectUrl",
						["property_value"] = $"https://github.com/{context.FullName}"
					}
				}));
		}

		return Task.FromResult(Pass($"All {projects.Count} published project(s) have PackageProjectUrl set."));
	}
}
