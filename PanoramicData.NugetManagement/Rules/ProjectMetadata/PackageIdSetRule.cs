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
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var csproj = context.FindPrimaryProjectFile();
		if (csproj is null)
		{
			return Task.FromResult(Pass("No primary project found — skipping PackageId check."));
		}

		var content = context.GetFileContent(csproj);
		if (!Contains(content, "<PackageId>"))
		{
			return Task.FromResult(Fail(
				$"{csproj} does not have PackageId set.",
				new RuleAdvisory
				{
					Summary = "Add <PackageId>YourPackageId</PackageId> to the .csproj.",
					Detail = $"The project `{csproj}` does not have `<PackageId>` set. Add `<PackageId>YourPackageId</PackageId>` to a `<PropertyGroup>`.",
					Data = new()
					{
						["file"] = csproj,
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "PackageId",
						["property_value"] = Path.GetFileNameWithoutExtension(csproj)
					}
				}));
		}

		return Task.FromResult(Pass("Primary project has PackageId set."));
	}
}