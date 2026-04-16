using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that non-primary, non-test projects (tools, generators, etc.) are explicitly
/// opted out of NuGet packaging via &lt;IsPackable&gt;false&lt;/IsPackable&gt;.
/// Only the primary project (whose filename matches the repository name) should be published to NuGet.
/// </summary>
public class NonPrimaryProjectsAreNonPackableRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-09";

	/// <inheritdoc />
	public override string RuleName => "Non-primary projects are explicitly non-packable";

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

		var ancillary = context.FindNonPrimaryNonTestProjectFiles().ToList();
		if (ancillary.Count == 0)
		{
			return Task.FromResult(Pass("No ancillary projects found — nothing to check."));
		}

		// .Cli projects are treated as dotnet tools and are expected to be packable.
		var requiredNonPackable = ancillary
			.Where(csproj => !IsCliProject(csproj))
			.ToList();

		if (requiredNonPackable.Count == 0)
		{
			return Task.FromResult(Pass("No non-cli ancillary projects found — nothing to check."));
		}

		var missing = requiredNonPackable
			.Where(csproj =>
			{
				var content = context.GetFileContent(csproj);
				return content is not null && !IsExplicitlyNonPackable(content);
			})
			.ToList();

		return Task.FromResult(missing.Count == 0
			? Pass("All non-primary non-cli projects have <IsPackable>false</IsPackable>.")
			: Fail(
				$"{missing.Count} ancillary non-cli project(s) are missing <IsPackable>false</IsPackable>: {string.Join(", ", missing)}.",
				new RuleAdvisory
				{
					Summary = "Add <IsPackable>false</IsPackable> to each non-primary non-cli project.",
					Detail = $"The following projects are not the primary NuGet package for this repository and do not have `<IsPackable>false</IsPackable>`. Add it to each to prevent accidental publishing. Projects ending with `.Cli` are intentionally exempt: {string.Join(", ", missing)}.",
					Data = new()
					{
						["missing_projects"] = missing.ToArray()
					}
				}));
	}

	private static bool IsCliProject(string csprojPath)
		=> Path.GetFileNameWithoutExtension(csprojPath)
			.EndsWith(".Cli", StringComparison.OrdinalIgnoreCase);
}
