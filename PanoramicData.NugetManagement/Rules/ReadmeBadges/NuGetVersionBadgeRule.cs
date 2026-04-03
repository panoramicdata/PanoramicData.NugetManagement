using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that README.md contains a NuGet version badge.
/// </summary>
public class NuGetVersionBadgeRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "README-03";

    /// <inheritdoc />
    public override string RuleName => "NuGet version badge in README";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.ReadmeBadges;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        if (!context.Options.IsPackable)
        {
            return Task.FromResult(Pass("Repository is not packable — skipping."));
        }

        var content = context.GetFileContent("README.md");
        return Task.FromResult(Contains(content, "nuget.org/packages")
            ? Pass("NuGet version badge found in README.md.")
            : Fail(
                "README.md does not contain a NuGet version badge.",
                new RuleAdvisory
                {
                    Summary = "Add a NuGet version badge linking to nuget.org/packages/<PackageId>.",
                    Detail = "The `README.md` does not contain a NuGet version badge. Add a badge linking to `nuget.org/packages/<PackageId>`.",
                    Data = new() { ["file"] = "README.md" }
                }));
    }
}
