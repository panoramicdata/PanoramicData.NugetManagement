namespace PanoramicData.NugetManagement.Web.Remediations.CodeQuality;

/// <summary>Adds tab indentation to .editorconfig.</summary>
public sealed class TabIndentationRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "CQ-04";
}
