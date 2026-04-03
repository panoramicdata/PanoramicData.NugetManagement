using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that nuget-key.txt is listed in .gitignore.
/// </summary>
public class NugetKeyGitignoredRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "REPO-02";

    /// <inheritdoc />
    public override string RuleName => "nuget-key.txt is gitignored";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.RepositoryHygiene;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Error;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        var content = context.GetFileContent(".gitignore");
        return Task.FromResult(Contains(content, "nuget-key.txt")
            ? Pass("nuget-key.txt is in .gitignore.")
            : Fail(
                "nuget-key.txt is not in .gitignore — risk of leaking NuGet API key.",
                new RuleAdvisory
                {
                    Summary = "Add 'nuget-key.txt' to .gitignore.",
                    Detail = "The `.gitignore` file does not include `nuget-key.txt`. This risks leaking a NuGet API key. Add `nuget-key.txt` to `.gitignore`.",
                    Data = new()
                    {
                        ["file"] = ".gitignore",
                        ["remediation_type"] = "append_line",
                        ["line_content"] = "nuget-key.txt"
                    }
                }));
    }
}
