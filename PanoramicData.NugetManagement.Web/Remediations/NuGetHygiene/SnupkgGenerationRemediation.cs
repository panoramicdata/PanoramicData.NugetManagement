using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>Adds IncludeSymbols and SymbolPackageFormat for snupkg generation.</summary>
public sealed class SnupkgGenerationRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "PKG-01";

	/// <inheritdoc />
	protected override void ApplyCore(
		string localPath,
		RuleResult result,
		Dictionary<string, object> data,
		string remediationType,
		List<string> applied,
		Action<string>? onOutput)
	{
		var file = data.TryGetValue("file", out var fObj) && fObj is string f ? f : null;
		if (file is null)
		{
			return;
		}

		RemediationHelpers.EnsureXmlProperty(localPath, file, "IncludeSymbols", "true", result, applied, onOutput);
		RemediationHelpers.EnsureXmlProperty(localPath, file, "SymbolPackageFormat", "snupkg", result, applied, onOutput);
	}
}
