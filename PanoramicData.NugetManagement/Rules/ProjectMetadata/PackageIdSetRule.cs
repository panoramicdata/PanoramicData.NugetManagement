using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageId is set in packable projects.
/// </summary>
public class PackageIdSetRule : ProjectPropertySetRuleBase
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
	protected override string PropertyName => "PackageId";

	/// <inheritdoc />
	protected override string Summary => "Add <PackageId>YourPackageId</PackageId> to the .csproj.";

	/// <inheritdoc />
	protected override string Detail(List<string> missing)
		=> StandardDetail(missing, "<PackageId>YourPackageId</PackageId>");

	/// <inheritdoc />
	protected override Dictionary<string, object> AdvisoryData(RepositoryContext context, List<string> missing)
	{
		var data = SetPropertyData(missing, Path.GetFileNameWithoutExtension(missing[0]));
		data.Remove("projects");
		data["file"] = missing[0];
		data["projects_missing"] = missing.ToArray();
		return data;
	}
}
