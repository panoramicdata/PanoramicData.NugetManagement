using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the CI workflow uses the latest actions/checkout version.
/// </summary>
public class CiActionsCheckoutVersionRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "CI-05";

    /// <inheritdoc />
    public override string RuleName => "CI uses latest actions/checkout";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.CiCd;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        var ciWorkflowPath = CiWorkflowPathResolver.Resolve(context);
        var content = context.GetFileContent(ciWorkflowPath);
        if (content is null)
        {
            return Task.FromResult(Fail(
                "CI workflow not found.",
                new RuleAdvisory
                {
                    Summary = $"Create `{ciWorkflowPath}` and use `actions/checkout@{Standards.LatestActionsCheckoutVersion}`",
                    Detail = $"Create `{ciWorkflowPath}` using `actions/checkout@{Standards.LatestActionsCheckoutVersion}`.",
                    Data = new()
                    {
                        ["expected_path"] = ciWorkflowPath,
                        ["latest_version"] = Standards.LatestActionsCheckoutVersion
                    }
                }));
        }

        var expected = $"actions/checkout@{Standards.LatestActionsCheckoutVersion}";
        return Task.FromResult(Contains(content, expected)
            ? Pass($"CI uses {expected}.")
            : Fail(
                $"CI does not use {expected}.",
                new RuleAdvisory
                {
                    Summary = "Update actions/checkout to latest major version",
                    Detail = $"Update the checkout step to `uses: {expected}`.",
                    Data = new()
                    {
                        ["workflow_file"] = ciWorkflowPath,
                        ["latest_version"] = Standards.LatestActionsCheckoutVersion
                    }
                }));
    }
}
