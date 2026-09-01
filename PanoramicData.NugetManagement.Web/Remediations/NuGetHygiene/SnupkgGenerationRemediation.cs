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
		// The rule names the offending projects, and may name more than one; a single "file" is what an
		// advisory that has narrowed it to one writes. Reading only the latter meant this remediation
		// silently did nothing for every failure the rule actually raises.
		foreach (var file in ReadStrings(data, "file", "projects"))
		{
			RemediationHelpers.EnsureXmlProperty(localPath, file, "IncludeSymbols", "true", result, applied, onOutput);
			RemediationHelpers.EnsureXmlProperty(localPath, file, "SymbolPackageFormat", "snupkg", result, applied, onOutput);
		}
	}
}
