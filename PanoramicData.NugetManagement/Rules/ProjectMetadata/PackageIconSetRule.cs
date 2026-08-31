using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageIcon is set in packable projects.
/// </summary>
public class PackageIconSetRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-05";

	/// <inheritdoc />
	public override string RuleName => "PackageIcon set";

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
			.Where(csproj => !HasMsBuildProperty(context.GetFileContent(csproj), "PackageIcon"))
			.ToList();

		if (missing.Count > 0)
		{
			return Task.FromResult(Fail(
				$"{string.Join(", ", missing)} do(es) not have PackageIcon set.",
				new RuleAdvisory
				{
					Summary = "Set <PackageIcon> with a corresponding <None Include> in the .csproj.",
					Detail = $"These published projects do not have `<PackageIcon>` set: {string.Join(", ", missing)}. Add `<PackageIcon>Logo.png</PackageIcon>` to a `<PropertyGroup>` and include the icon file via `<None Include=\"Logo.png\" Pack=\"true\" PackagePath=\"\\\" />` in an `<ItemGroup>`.",
					Data = new() { ["projects"] = missing.ToArray() }
				}));
		}

		return Task.FromResult(Pass($"All {projects.Count} published project(s) have PackageIcon set."));
	}
}
