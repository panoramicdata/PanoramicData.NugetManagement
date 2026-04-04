namespace PanoramicData.NugetManagement.Web.Remediations.CentralPackageManagement;

/// <summary>Removes Version attributes from PackageReference elements in .csproj files.</summary>
public sealed class CpmNoVersionInCsprojRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "CPM-02";
}
