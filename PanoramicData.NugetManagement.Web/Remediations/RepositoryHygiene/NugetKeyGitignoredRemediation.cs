namespace PanoramicData.NugetManagement.Web.Remediations.RepositoryHygiene;

/// <summary>Adds nuget_key gitignore entry.</summary>
public sealed class NugetKeyGitignoredRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "REPO-02";
}
