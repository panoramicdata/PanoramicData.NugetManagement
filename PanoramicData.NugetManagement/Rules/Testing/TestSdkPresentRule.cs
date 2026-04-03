using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that Microsoft.NET.Test.Sdk is referenced.
/// </summary>
public class TestSdkPresentRule : RuleBase
{
    /// <inheritdoc />
    public override string RuleId => "TST-03";

    /// <inheritdoc />
    public override string RuleName => "Microsoft.NET.Test.Sdk referenced";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.Testing;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Error;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        var dirPackages = context.GetFileContent("Directory.Packages.props");
        if (Contains(dirPackages, "Microsoft.NET.Test.Sdk"))
        {
            return Task.FromResult(Pass("Microsoft.NET.Test.Sdk is referenced."));
        }

        var testProjects = context.FindFiles(".csproj")
            .Where(f => f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

        foreach (var tp in testProjects)
        {
            var content = context.GetFileContent(tp);
            if (Contains(content, "Microsoft.NET.Test.Sdk"))
            {
                return Task.FromResult(Pass("Microsoft.NET.Test.Sdk is referenced."));
            }
        }

        return Task.FromResult(Fail(
            "Microsoft.NET.Test.Sdk is not referenced.",
            new RuleAdvisory
            {
                Summary = "Add Microsoft.NET.Test.Sdk to the test project.",
                Detail = "Microsoft.NET.Test.Sdk is not referenced in `Directory.Packages.props` or any test project. Add a reference to enable test discovery and execution.",
                Data = new() { ["expected_package"] = "Microsoft.NET.Test.Sdk" }
            }));
    }
}
