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
		var notApplicable = PackagingCheckApplies(context, out var projects);
		if (notApplicable is not null)
		{
			return Task.FromResult(notApplicable);
		}

		var missing = projects
			.Where(csproj => !HasMsBuildProperty(context.GetFileContent(csproj), "RepositoryUrl"))
			.ToList();

		if (missing.Count > 0)
		{
			return Task.FromResult(Fail(
				$"{string.Join(", ", missing)} do(es) not have RepositoryUrl set.",
				new RuleAdvisory
				{
					Summary = "Add <RepositoryUrl>https://github.com/org/repo</RepositoryUrl> to the .csproj.",
					Detail = $"These published projects do not have `<RepositoryUrl>` set: {string.Join(", ", missing)}. Add `<RepositoryUrl>https://github.com/org/repo</RepositoryUrl>` to a `<PropertyGroup>`.",
					Data = new()
					{
						["projects"] = missing.ToArray(),
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "RepositoryUrl",
						["property_value"] = $"https://github.com/{context.FullName}"
					}
				}));
		}

		return Task.FromResult(Pass($"All {projects.Count} published project(s) have RepositoryUrl set."));
	}
}