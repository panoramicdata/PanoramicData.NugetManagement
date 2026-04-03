using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that Centralized Package Management is enabled.
/// </summary>
public class CpmEnabledRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "CPM-01";

    /// <inheritdoc />
    public override string RuleName => "CPM enabled";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.CentralPackageManagement;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Error;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        var content = context.GetFileContent("Directory.Packages.props");
        if (content is null)
        {
            return Task.FromResult(Fail(
                "Directory.Packages.props not found.",
                new RuleAdvisory
                {
                    Summary = "Create Directory.Packages.props with `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`",
                    Detail = "Create a `Directory.Packages.props` file at the repository root with Central Package Management enabled.",
                    Data = new()
                    {
                        ["remediation_type"] = "ensure_xml_property",
                        ["file"] = "Directory.Packages.props",
                        ["property_name"] = "ManagePackageVersionsCentrally",
                        ["property_value"] = "true"
                    }
                }));
        }

        return Task.FromResult(Contains(content, "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>")
            ? Pass("Centralized Package Management is enabled.")
            : Fail(
                "Directory.Packages.props does not enable ManagePackageVersionsCentrally.",
                new RuleAdvisory
                {
                    Summary = "Enable `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` in Directory.Packages.props",
                    Detail = "Add `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` to `Directory.Packages.props`.",
                    Data = new()
                    {
                        ["remediation_type"] = "ensure_xml_property",
                        ["file"] = "Directory.Packages.props",
                        ["property_name"] = "ManagePackageVersionsCentrally",
                        ["property_value"] = "true"
                    }
                }));
    }
}
