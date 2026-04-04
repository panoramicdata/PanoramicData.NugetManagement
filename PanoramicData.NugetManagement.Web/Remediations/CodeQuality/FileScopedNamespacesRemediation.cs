namespace PanoramicData.NugetManagement.Web.Remediations.CodeQuality;

/// <summary>Adds file-scoped namespace preference to .editorconfig.</summary>
public sealed class FileScopedNamespacesRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "CQ-02";
}
