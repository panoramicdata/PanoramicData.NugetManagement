using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that GeneratePackageOnBuild is enabled in packable projects.
/// </summary>
public class GeneratePackageOnBuildRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "PKG-02";

    /// <inheritdoc />
    public override string RuleName => "GeneratePackageOnBuild enabled";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        if (!context.Options.IsPackable)
        {
            return Task.FromResult(Pass("Repository is not packable — skipping."));
        }

        var csprojFiles = context.FindFiles(".csproj")
            .Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var csproj in csprojFiles)
        {
            var content = context.GetFileContent(csproj);
            if (content is not null && !Contains(content, "<GeneratePackageOnBuild>true</GeneratePackageOnBuild>"))
            {
                return Task.FromResult(Fail(
                    $"{csproj} does not enable GeneratePackageOnBuild.",
                    new RuleAdvisory
                    {
                        Summary = "Add <GeneratePackageOnBuild>true</GeneratePackageOnBuild> to the .csproj.",
                        Detail = $"The project `{csproj}` does not enable `GeneratePackageOnBuild`. Add `<GeneratePackageOnBuild>true</GeneratePackageOnBuild>` to a `<PropertyGroup>`.",
                        Data = new()
                        {
                            ["file"] = csproj,
                            ["remediation_type"] = "ensure_csproj_property",
                            ["property_name"] = "GeneratePackageOnBuild",
                            ["property_value"] = "true"
                        }
                    }));
            }
        }

        return Task.FromResult(Pass("All packable projects have GeneratePackageOnBuild enabled."));
    }
}
