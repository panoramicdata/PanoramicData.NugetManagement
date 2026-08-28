using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that snupkg symbol generation is enabled in packable projects.
/// </summary>
public class SnupkgGenerationRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-01";

	/// <inheritdoc />
	public override string RuleName => "snupkg symbol generation enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

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
			.Where(csproj =>
			{
				var content = context.GetFileContent(csproj);
				return !HasMsBuildProperty(content, "IncludeSymbols", "true")
					|| !HasMsBuildProperty(content, "SymbolPackageFormat", "snupkg");
			})
			.ToList();

		if (missing.Count > 0)
		{
			return Task.FromResult(Fail(
				$"{string.Join(", ", missing)} do(es) not enable snupkg generation.",
				new RuleAdvisory
				{
					Summary = "Add <IncludeSymbols>true</IncludeSymbols> and <SymbolPackageFormat>snupkg</SymbolPackageFormat> to the .csproj.",
					Detail = $"These published projects do not enable snupkg symbol package generation: {string.Join(", ", missing)}. Add both `<IncludeSymbols>true</IncludeSymbols>` and `<SymbolPackageFormat>snupkg</SymbolPackageFormat>` to a `<PropertyGroup>`.",
					Data = new()
					{
						["projects"] = missing.ToArray(),
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "IncludeSymbols",
						["property_value"] = "true"
					}
				}));
		}

		return Task.FromResult(Pass($"All {projects.Count} published project(s) have snupkg generation enabled."));
	}
}