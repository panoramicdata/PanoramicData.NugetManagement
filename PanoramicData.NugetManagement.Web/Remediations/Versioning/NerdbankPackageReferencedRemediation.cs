namespace PanoramicData.NugetManagement.Web.Remediations.Versioning;

/// <summary>Adds Nerdbank.GitVersioning package reference.</summary>
public sealed class NerdbankPackageReferencedRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "VER-02";
}
