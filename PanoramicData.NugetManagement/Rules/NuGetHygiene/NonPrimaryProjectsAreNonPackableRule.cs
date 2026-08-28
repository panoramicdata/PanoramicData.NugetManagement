using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that non-test projects the repository does not publish (tools, generators, samples) are
/// explicitly opted out of NuGet packaging via &lt;IsPackable&gt;false&lt;/IsPackable&gt;, so that what
/// is published is a decision rather than an accident of the SDK default.
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
			return Task.FromResult(Pass("Repository is not packable - skipping."));
		}

		// Ancillary now means "not one of the projects this repository publishes", decided by what
		// each project declares. Deriving it from the project name meant that in a repository whose
		// package project is named something else, the package itself was reported here as an
		// ancillary project that ought to be non-packable.
		var ancillary = context.FindNonPackableProjectFiles().ToList();
		if (ancillary.Count == 0)
		{
			return Task.FromResult(NotApplicable("No ancillary projects found - nothing to check."));
		}

		// .Cli projects are treated as dotnet tools and are expected to be packable.
		var requiredNonPackable = ancillary
			.Where(csproj => !IsCliProject(csproj))
			.ToList();

		if (requiredNonPackable.Count == 0)
		{
			return Task.FromResult(NotApplicable("No non-cli ancillary projects found - nothing to check."));
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
						["missing_projects"] = missing.ToArray(),
						["remediation_type"] = "ensure_csproj_property",
						["projects"] = missing.ToArray(),
						["property_name"] = "IsPackable",
						["property_value"] = "false"
					}
				}));
	}

	private static bool IsCliProject(string csprojPath)
		=> Path.GetFileNameWithoutExtension(csprojPath)
			.EndsWith(".Cli", StringComparison.OrdinalIgnoreCase);
}
