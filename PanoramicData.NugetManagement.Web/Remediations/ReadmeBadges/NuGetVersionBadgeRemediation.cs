namespace PanoramicData.NugetManagement.Web.Remediations.ReadmeBadges;

/// <summary>Prepends NuGet version badge to README.md.</summary>
public sealed class NuGetVersionBadgeRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "README-03";
}
