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
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var csproj = context.FindPrimaryProjectFile();
		if (csproj is null)
		{
			return Task.FromResult(Pass("No primary project found — skipping snupkg check."));
		}

		var content = context.GetFileContent(csproj);
		if (!Contains(content, "<IncludeSymbols>true</IncludeSymbols>") ||
			!Contains(content, "<SymbolPackageFormat>snupkg</SymbolPackageFormat>"))
		{
			return Task.FromResult(Fail(
				$"{csproj} does not enable snupkg generation.",
				new RuleAdvisory
				{
					Summary = "Add <IncludeSymbols>true</IncludeSymbols> and <SymbolPackageFormat>snupkg</SymbolPackageFormat> to the .csproj.",
					Detail = $"The project `{csproj}` does not enable snupkg symbol package generation. Add both `<IncludeSymbols>true</IncludeSymbols>` and `<SymbolPackageFormat>snupkg</SymbolPackageFormat>` to a `<PropertyGroup>`.",
					Data = new()
					{
						["file"] = csproj,
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "IncludeSymbols",
						["property_value"] = "true"
					}
				}));
		}

		return Task.FromResult(Pass("Primary project has snupkg generation enabled."));
	}
}