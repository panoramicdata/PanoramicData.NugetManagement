using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageReadmeFile is set.
/// </summary>
public class PackageReadmeFileRule : ProjectPropertySetRuleBase
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
	protected override string PropertyName => "PackageReadmeFile";

	/// <inheritdoc />
	protected override string Summary => "Add <PackageReadmeFile>README.md</PackageReadmeFile> and pack the README.md via <None Include>.";

	/// <inheritdoc />
	protected override string FailureMessage(List<string> missing)
		=> $"{string.Join(", ", missing)} do(es) not set PackageReadmeFile.";

	/// <inheritdoc />
	protected override string Detail(List<string> missing)
		=> $"These published projects do not set `PackageReadmeFile`: {string.Join(", ", missing)}. Add `<PackageReadmeFile>README.md</PackageReadmeFile>` to a `<PropertyGroup>` and include `<None Include=\"..\\README.md\" Pack=\"true\" PackagePath=\"\\\"/>` in an `<ItemGroup>`.";

	/// <inheritdoc />
	protected override Dictionary<string, object> AdvisoryData(RepositoryContext context, List<string> missing)
		=> SetPropertyData(missing, "README.md");
}
