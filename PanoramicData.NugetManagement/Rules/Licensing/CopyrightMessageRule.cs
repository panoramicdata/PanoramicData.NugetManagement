using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that Copyright is set in Directory.Build.props with the expected holder.
/// </summary>
public class CopyrightMessageRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "LIC-03";

    /// <inheritdoc />
    public override string RuleName => "Copyright message in Directory.Build.props";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.Licensing;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Error;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        var expected = context.Options.ExpectedCopyrightHolder;
        var content = context.GetFileContent("Directory.Build.props");
        if (content is null)
        {
            return Task.FromResult(Fail(
                "Directory.Build.props not found.",
                new RuleAdvisory
                {
                    Summary = $"Create Directory.Build.props with <Copyright> containing \"{expected}\".",
                    Detail = $"No `Directory.Build.props` file was found. Create one with `<Copyright>Copyright © $(Year) {expected}</Copyright>`.",
                    Data = new()
                    {
                        ["file"] = "Directory.Build.props",
                        ["expected_holder"] = expected,
                        ["remediation_type"] = "ensure_xml_property",
                        ["property_name"] = "Copyright",
                        ["property_value"] = $"Copyright © {expected}"
                    }
                }));
        }

        var hasCopyright = Contains(content, "<Copyright>") && Contains(content, expected);
        return Task.FromResult(hasCopyright
            ? Pass($"Copyright message found with \"{expected}\".")
            : Fail(
                $"Directory.Build.props does not contain Copyright with expected holder \"{expected}\".",
                new RuleAdvisory
                {
                    Summary = $"Add <Copyright>Copyright © $(Year) {expected}</Copyright> to Directory.Build.props.",
                    Detail = $"`Directory.Build.props` exists but does not contain a `<Copyright>` element with `{expected}`. Add `<Copyright>Copyright © $(Year) {expected}</Copyright>`.",
                    Data = new()
                    {
                        ["file"] = "Directory.Build.props",
                        ["expected_holder"] = expected,
                        ["remediation_type"] = "ensure_xml_property",
                        ["property_name"] = "Copyright",
                        ["property_value"] = $"Copyright © {expected}"
                    }
                }));
    }
}
