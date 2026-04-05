using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations.ProjectMetadata;

/// <summary>Adds Authors and Company to Directory.Build.props.</summary>
public sealed class AuthorsAndCompanyRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "META-03";

	/// <inheritdoc />
	protected override void ApplyCore(
		string localPath,
		RuleResult result,
		Dictionary<string, object> data,
		string remediationType,
		List<string> applied,
		Action<string>? onOutput)
	{
		var file = data.TryGetValue("file", out var fObj) && fObj is string f ? f : "Directory.Build.props";
		var expected = data.TryGetValue("expected_holder", out var hObj) && hObj is string h ? h : "Panoramic Data Limited";

		RemediationHelpers.EnsureXmlProperty(localPath, file, "Authors", expected, result, applied, onOutput);
		RemediationHelpers.EnsureXmlProperty(localPath, file, "Company", expected, result, applied, onOutput);
	}
}
