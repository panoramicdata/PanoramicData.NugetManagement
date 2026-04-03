using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that Nerdbank.GitVersioning is referenced.
/// </summary>
public class NerdbankPackageReferencedRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "VER-02";

    /// <inheritdoc />
    public override string RuleName => "Nerdbank.GitVersioning referenced";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.Versioning;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Error;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        var dirPackages = context.GetFileContent("Directory.Packages.props");
        var allCsprojs = context.FindFiles(".csproj")
            .Select(context.GetFileContent)
            .Where(c => c is not null);

        var inCpm = Contains(dirPackages, "Nerdbank.GitVersioning");
        var inCsproj = allCsprojs.Any(c => Contains(c, "Nerdbank.GitVersioning"));

        return Task.FromResult(inCpm || inCsproj
            ? Pass("Nerdbank.GitVersioning is referenced.")
            : Fail(
                "Nerdbank.GitVersioning is not referenced in Directory.Packages.props or any .csproj.",
                new RuleAdvisory
                {
                    Summary = "Add a PackageVersion for Nerdbank.GitVersioning to Directory.Packages.props.",
                    Detail = "Nerdbank.GitVersioning is not referenced in `Directory.Packages.props` or any `.csproj`. Add a `<PackageVersion>` entry for `Nerdbank.GitVersioning`.",
                    Data = new() { ["expected_package"] = "Nerdbank.GitVersioning" }
                }));
    }
}
