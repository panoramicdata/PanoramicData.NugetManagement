using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that .csproj files do not have Version= attributes on PackageReference elements.
/// </summary>
public partial class CpmNoVersionInCsprojRule : RuleBase
{
    [GeneratedRegex(@"<PackageReference\s+[^>]*Version\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReferenceVersionPattern();
    /// <inheritdoc />
    public override string RuleId => "CPM-02";

    /// <inheritdoc />
    public override string RuleName => "No Version in .csproj PackageReferences";

    /// <inheritdoc />
    public override AssessmentCategory Category => AssessmentCategory.CentralPackageManagement;

    /// <inheritdoc />
    public override AssessmentSeverity Severity => AssessmentSeverity.Error;

    /// <inheritdoc />
    public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
    {
        var csprojFiles = context.FindFiles(".csproj").ToList();
        var violations = new List<string>();

        foreach (var csproj in csprojFiles)
        {
            var content = context.GetFileContent(csproj);
            if (content is null)
            {
                continue;
            }

            // Check for PackageReference with Version= attribute (but not PackageVersion which is correct)
            if (PackageReferenceVersionPattern().IsMatch(content))
            {
                violations.Add(csproj);
            }
        }

        return Task.FromResult(violations.Count == 0
            ? Pass("No .csproj files have Version= on PackageReference elements.")
            : Fail(
                $"The following .csproj files have Version= on PackageReference elements: {string.Join(", ", violations)}",
                new RuleAdvisory
                {
                    Summary = "Remove `Version` attributes from PackageReference elements; move versions to Directory.Packages.props",
                    Detail = "Remove all `Version` attributes from `<PackageReference>` elements in the listed .csproj files. Versions should be managed centrally in `Directory.Packages.props`.",
                    Data = new() { ["projects_with_versions"] = violations.ToArray() }
                }));
    }
}
