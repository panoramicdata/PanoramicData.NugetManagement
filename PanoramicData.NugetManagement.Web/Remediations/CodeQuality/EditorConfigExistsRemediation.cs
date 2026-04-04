namespace PanoramicData.NugetManagement.Web.Remediations.CodeQuality;

/// <summary>Creates .editorconfig from template.</summary>
public sealed class EditorConfigExistsRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "CQ-01";
}
