using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that RepositoryUrl is set in packable projects.
/// </summary>
public class RepositoryUrlSetRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-02";

	/// <inheritdoc />
	public override string RuleName => "RepositoryUrl set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ProjectMetadata;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<RepositoryUrl>"))
			{
				return Task.FromResult(Fail(
					$"{csproj} does not have RepositoryUrl set.",
					new RuleAdvisory
					{
						Summary = "Add <RepositoryUrl>https://github.com/org/repo</RepositoryUrl> to the .csproj.",
						Detail = $"The project `{csproj}` does not have `<RepositoryUrl>` set. Add `<RepositoryUrl>https://github.com/org/repo</RepositoryUrl>` to a `<PropertyGroup>`.",
						Data = new()
						{
							["file"] = csproj,
							["remediation_type"] = "ensure_csproj_property",
							["property_name"] = "RepositoryUrl",
							["property_value"] = $"https://github.com/{context.FullName}"
						}
					}));
			}
		}

		return Task.FromResult(Pass("All packable projects have RepositoryUrl set."));
	}
}
