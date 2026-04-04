namespace PanoramicData.NugetManagement.Web.Remediations.Versioning;

/// <summary>Creates global.json from template.</summary>
public sealed class GlobalJsonRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "VER-03";
}
