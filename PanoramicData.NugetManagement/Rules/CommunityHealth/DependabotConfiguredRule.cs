using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that .github/dependabot.yml exists.
/// </summary>
public class DependabotConfiguredRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "COM-03";

	/// <inheritdoc />
	public override string RuleName => "Dependabot configured";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.DependencyAutomation;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var hasDependabot = context.FileExists(".github/dependabot.yml") ||
							context.FileExists(".github/dependabot.yaml");
		var hasRenovate = context.FileExists("renovate.json") ||
						  context.FileExists(".github/renovate.json");

		return Task.FromResult(hasDependabot || hasRenovate
			? Pass("Dependency update automation configured (Dependabot or Renovate).")
			: Fail(
				"No dependency update automation found (Dependabot or Renovate).",
				new RuleAdvisory
				{
					Summary = "Create `.github/dependabot.yml` with NuGet and GitHub Actions ecosystems",
					Detail = "Create a `.github/dependabot.yml` file configuring automatic dependency updates for both NuGet packages and GitHub Actions.",
					Data = new()
					{
						["expected_path"] = ".github/dependabot.yml",
						["template_content"] = Standards.DependabotYmlContent
					}
				}));
	}
}
