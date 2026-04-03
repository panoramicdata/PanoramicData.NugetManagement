using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageReadmeFile is set.
/// </summary>
public class PackageReadmeFileRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "PKG-03";

    /// <inheritdoc />
    public override string RuleName => "PackageReadmeFile set";

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
            if (content is not null && !Contains(content, "<PackageReadmeFile>"))
            {
                return Task.FromResult(Fail(
                    $"{csproj} does not set PackageReadmeFile.",
                    new RuleAdvisory
                    {
                        Summary = "Add <PackageReadmeFile>README.md</PackageReadmeFile> and pack the README.md via <None Include>.",
                        Detail = $"The project `{csproj}` does not set `PackageReadmeFile`. Add `<PackageReadmeFile>README.md</PackageReadmeFile>` to a `<PropertyGroup>` and include `<None Include=\"..\\README.md\" Pack=\"true\" PackagePath=\"\\\"/>` in an `<ItemGroup>`.",
                        Data = new()
                        {
                            ["file"] = csproj,
                            ["remediation_type"] = "ensure_csproj_property",
                            ["property_name"] = "PackageReadmeFile",
                            ["property_value"] = "README.md"
                        }
                    }));
            }
        }

        return Task.FromResult(Pass("All packable projects have PackageReadmeFile set."));
    }
}
