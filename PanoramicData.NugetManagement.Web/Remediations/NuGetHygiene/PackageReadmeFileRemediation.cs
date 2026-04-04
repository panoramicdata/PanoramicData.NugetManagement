namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>Adds PackageReadmeFile to Directory.Build.props.</summary>
public sealed class PackageReadmeFileRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "PKG-03";
}
