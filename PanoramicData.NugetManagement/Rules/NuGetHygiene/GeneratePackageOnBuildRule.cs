using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that GeneratePackageOnBuild is enabled in packable projects.
/// </summary>
public class GeneratePackageOnBuildRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-02";

	/// <inheritdoc />
	public override string RuleName => "GeneratePackageOnBuild enabled";

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
			.Where(csproj => !HasMsBuildProperty(context.GetFileContent(csproj), "GeneratePackageOnBuild", "true"))
			.ToList();

		if (missing.Count > 0)
		{
			return Task.FromResult(Fail(
				$"{string.Join(", ", missing)} do(es) not enable GeneratePackageOnBuild.",
				new RuleAdvisory
				{
					Summary = "Add <GeneratePackageOnBuild>true</GeneratePackageOnBuild> to the .csproj.",
					Detail = $"These published projects do not enable `GeneratePackageOnBuild`: {string.Join(", ", missing)}. Add `<GeneratePackageOnBuild>true</GeneratePackageOnBuild>` to a `<PropertyGroup>`.",
					Data = new()
					{
						["projects"] = missing.ToArray(),
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "GeneratePackageOnBuild",
						["property_value"] = "true"
					}
				}));
		}

		return Task.FromResult(Pass($"All {projects.Count} published project(s) have GeneratePackageOnBuild enabled."));
	}
}