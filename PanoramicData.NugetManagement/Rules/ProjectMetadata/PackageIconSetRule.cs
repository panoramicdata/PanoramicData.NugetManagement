using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageIcon is set in packable projects.
/// </summary>
public class PackageIconSetRule : ProjectPropertySetRuleBase
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
	protected override string PropertyName => "PackageIcon";

	/// <inheritdoc />
	protected override string Summary => "Set <PackageIcon> with a corresponding <None Include> in the .csproj.";

	/// <inheritdoc />
	protected override string Detail(List<string> missing)
		=> $"These published projects do not have `<PackageIcon>` set: {string.Join(", ", missing)}. Add `<PackageIcon>Logo.png</PackageIcon>` to a `<PropertyGroup>` and include the icon file via `<None Include=\"Logo.png\" Pack=\"true\" PackagePath=\"\\\" />` in an `<ItemGroup>`.";
}
