using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that snupkg symbol generation is enabled in packable projects.
/// </summary>
public class SnupkgGenerationRule : ProjectPropertySetRuleBase
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
	protected override string PropertyName => "IncludeSymbols";

	/// <inheritdoc />
	protected override string Summary => "Add <IncludeSymbols>true</IncludeSymbols> and <SymbolPackageFormat>snupkg</SymbolPackageFormat> to the .csproj.";

	/// <inheritdoc />
	protected override bool IsMissing(RepositoryContext context, string csproj)
	{
		var content = context.GetFileContent(csproj);

		// No build output (a template) means an empty .snupkg, which NuGet refuses (NU5017).
		return !HasMsBuildProperty(content, "IncludeBuildOutput", "false")
			&& (!HasMsBuildProperty(content, "IncludeSymbols", "true")
				|| !HasMsBuildProperty(content, "SymbolPackageFormat", "snupkg"));
	}

	/// <inheritdoc />
	protected override string FailureMessage(List<string> missing)
		=> $"{string.Join(", ", missing)} do(es) not enable snupkg generation.";

	/// <inheritdoc />
	protected override string PassMessage(int projectCount)
		=> $"All {projectCount} published project(s) have snupkg generation enabled.";

	/// <inheritdoc />
	protected override string Detail(List<string> missing)
		=> $"These published projects do not enable snupkg symbol package generation: {string.Join(", ", missing)}. Add both `<IncludeSymbols>true</IncludeSymbols>` and `<SymbolPackageFormat>snupkg</SymbolPackageFormat>` to a `<PropertyGroup>`.";

	/// <inheritdoc />
	protected override Dictionary<string, object> AdvisoryData(RepositoryContext context, List<string> missing)
		=> SetPropertyData(missing, "true");
}
