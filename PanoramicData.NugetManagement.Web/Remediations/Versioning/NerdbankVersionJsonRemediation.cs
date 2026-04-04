namespace PanoramicData.NugetManagement.Web.Remediations.Versioning;

/// <summary>Creates version.json from template.</summary>
public sealed class NerdbankVersionJsonRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "VER-01";
}
