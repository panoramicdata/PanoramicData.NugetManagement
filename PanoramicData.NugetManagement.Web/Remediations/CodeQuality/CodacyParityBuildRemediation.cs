namespace PanoramicData.NugetManagement.Web.Remediations.CodeQuality;

/// <summary>Adds the opt-in Codacy parity build: targets, analyzer configuration and complexity threshold.</summary>
public sealed class CodacyParityBuildRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "CQ-10";
}
