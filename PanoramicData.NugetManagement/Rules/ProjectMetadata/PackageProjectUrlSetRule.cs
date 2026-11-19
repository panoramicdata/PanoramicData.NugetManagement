using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageProjectUrl is set in packable projects.
/// </summary>
public class PackageProjectUrlSetRule : ProjectPropertySetRuleBase
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
	protected override string PropertyName => "PackageProjectUrl";

	/// <inheritdoc />
	protected override string Summary => "Add <PackageProjectUrl>https://github.com/org/repo</PackageProjectUrl> to the .csproj.";

	/// <inheritdoc />
	protected override string Detail(List<string> missing)
		=> StandardDetail(missing, "<PackageProjectUrl>https://github.com/org/repo</PackageProjectUrl>");

	/// <inheritdoc />
	protected override Dictionary<string, object> AdvisoryData(RepositoryContext context, List<string> missing)
		=> SetPropertyData(missing, $"https://github.com/{context.FullName}");
}
