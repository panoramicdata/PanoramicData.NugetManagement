using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that coverlet.collector is referenced for code coverage.
/// </summary>
public class CoverletCollectorRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "TST-04";

    /// <inheritdoc />
    public override string RuleName => "coverlet.collector referenced";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.Testing;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        var dirPackages = context.GetFileContent("Directory.Packages.props");
        if (Contains(dirPackages, "coverlet.collector"))
        {
            return Task.FromResult(Pass("coverlet.collector is referenced."));
        }

        var testProjects = context.FindFiles(".csproj")
            .Where(f => f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(testProjects.Any(tp => Contains(context.GetFileContent(tp), "coverlet.collector"))
            ? Pass("coverlet.collector is referenced.")
            : Fail(
                "coverlet.collector is not referenced.",
                new RuleAdvisory
                {
                    Summary = "Add coverlet.collector to the test project for code coverage collection.",
                    Detail = "coverlet.collector is not referenced in `Directory.Packages.props` or any test project. Add it to enable code coverage collection.",
                    Data = new() { ["expected_package"] = "coverlet.collector" }
                }));
    }
}
