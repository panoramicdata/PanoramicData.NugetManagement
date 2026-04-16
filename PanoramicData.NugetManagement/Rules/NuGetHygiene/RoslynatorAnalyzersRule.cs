using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that Roslynator.Analyzers is not directly referenced in any project file.
/// </summary>
public class RoslynatorAnalyzersRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-08";

	/// <inheritdoc />
	public override string RuleName => "Roslynator.Analyzers not directly referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var violating = context
			.FindFiles(".csproj")
			.Where(f => Contains(context.GetFileContent(f), "Roslynator.Analyzers"))
			.ToList();

		if (violating.Count == 0)
		{
			return Task.FromResult(Pass("No project files reference Roslynator.Analyzers."));
		}

		var fileList = string.Join(", ", violating.Select(f => $"`{f}`"));
		return Task.FromResult(Fail(
			$"Roslynator.Analyzers is directly referenced in: {fileList}",
			new RuleAdvisory
			{
				Summary = "Remove the Roslynator.Analyzers PackageReference from all project files.",
				Detail = $"The package `Roslynator.Analyzers` should not be directly referenced in project files. Remove the `<PackageReference Include=\"Roslynator.Analyzers\" />` block from: {fileList}",
				Data = new()
				{
					["remediation_type"] = "remove_packagereference",
					["package_name"] = "Roslynator.Analyzers",
					["projects"] = violating.ToArray()
				}
			}));
	}
}
