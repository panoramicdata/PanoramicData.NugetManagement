using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the CI workflow uses fetch-depth: 0 for Nerdbank.GitVersioning.
/// </summary>
public class CiCheckoutFetchDepthRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "CI-04";

    /// <inheritdoc />
    public override string RuleName => "CI checkout uses fetch-depth: 0";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.CiCd;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Error;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);
        var content = context.GetFileContent(ciWorkflowPath);
        if (content is null)
        {
            return Task.FromResult(Fail(
                "CI workflow not found — cannot check fetch-depth.",
                new RuleAdvisory
                {
                    Summary = $"Create `{ciWorkflowPath}` and configure actions/checkout with `fetch-depth: 0`",
                    Detail = "Set `fetch-depth: 0` on the `actions/checkout` step so Nerdbank.GitVersioning can calculate the version from the full git history.",
                    Data = new() { ["expected_path"] = ciWorkflowPath }
                }));
        }

        return Task.FromResult(Contains(content, "fetch-depth: 0")
            ? Pass("CI checkout uses fetch-depth: 0.")
            : Fail(
                "CI checkout does not use fetch-depth: 0, which is required for Nerdbank.GitVersioning.",
                new RuleAdvisory
                {
                    Summary = "Set `fetch-depth: 0` on actions/checkout for NBGV version calculation",
                    Detail = "Add `with: fetch-depth: 0` to the `actions/checkout` step in the CI workflow. This is required for Nerdbank.GitVersioning to calculate the correct version.",
                    Data = new() { ["workflow_file"] = ciWorkflowPath }
                }));
    }
}
