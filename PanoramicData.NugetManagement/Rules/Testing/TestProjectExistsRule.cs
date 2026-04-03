using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a test project exists.
/// </summary>
public class TestProjectExistsRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "TST-01";

    /// <inheritdoc />
    public override string RuleName => "Test project exists";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.Testing;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Error;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        var hasTestProject = context.FindFiles(".csproj")
            .Any(f => f.Contains(".Test", StringComparison.OrdinalIgnoreCase) ||
                      f.Contains(".Tests", StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(hasTestProject
            ? Pass("Test project found.")
            : Fail(
                "No test project found (*.Test.csproj or *.Tests.csproj).",
                new RuleAdvisory
                {
                    Summary = "Create a test project using xUnit v3.",
                    Detail = "No test project was found matching `*.Test.csproj` or `*.Tests.csproj`. Create a test project using xUnit v3.",
                    Data = new() { ["expected_pattern"] = "*.Test.csproj or *.Tests.csproj" }
                }));
    }
}
