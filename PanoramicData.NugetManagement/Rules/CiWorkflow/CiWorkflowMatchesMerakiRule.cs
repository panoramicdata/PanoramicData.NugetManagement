using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that ci.yml matches the Meraki.Api trusted publishing workflow shape.
/// </summary>
public class CiWorkflowMatchesMerakiRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "CI-08";

    /// <inheritdoc />
    public override string RuleName => "CI workflow matches Meraki.Api standard";

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
                "CI workflow not found.",
                new RuleAdvisory
                {
                    Summary = "Create CI workflow matching the standard Trusted Publishing pattern",
                    Detail = "Copy the standard CI workflow from Meraki.Api and adapt only repository-specific project paths. The workflow must include tag triggers, artifact upload, NuGet login, and nuget push steps.",
                    Data = new() { ["expected_path"] = ciWorkflowPath }
                }));
        }

        var requiredSnippets = new[]
        {
            "tags: ['[0-9]*.[0-9]*.[0-9]*']",
            "uses: actions/upload-artifact@v4",
            "publish:",
            "if: startsWith(github.ref, 'refs/tags/')",
            "id-token: write",
            "uses: NuGet/login@v1",
            "dotnet nuget push ./artifacts/*.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }}"
        };

        var missing = requiredSnippets
            .Where(snippet => !Contains(content, snippet))
            .ToList();

        return Task.FromResult(missing.Count == 0
            ? Pass("CI workflow matches the Meraki.Api trusted publishing standard.")
            : Fail(
                "CI workflow does not match the Meraki.Api standard trusted publishing shape.",
                new RuleAdvisory
                {
                    Summary = "Update CI workflow to match the standard Trusted Publishing pattern",
                    Detail = $"Ensure `{ciWorkflowPath}` includes all standard sections for trusted publishing including tag triggers, artifact upload, NuGet login, and push.",
                    Data = new()
                    {
                        ["workflow_file"] = ciWorkflowPath,
                        ["missing_snippets"] = missing.ToArray()
                    }
                }));
    }
}
