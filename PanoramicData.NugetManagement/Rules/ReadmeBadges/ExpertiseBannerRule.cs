using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that README.md opens with the configured statement that Panoramic Data are API and .NET
/// experts who can help, linking to the contact and software services pages.
/// </summary>
public class ExpertiseBannerRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "README-05";

	/// <inheritdoc />
	public override string RuleName => "Expertise statement at the top of README";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ReadmeBadges;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var readme = context.GetFileContent("README.md");
		if (readme is null)
		{
			return Task.FromResult(NotApplicable("README.md is missing or not fetched; README-01 reports that."));
		}

		var banner = ReadmeExpertiseBanner.Render(context.Options.ExpertiseBannerTemplate, context.Name);
		if (ReadmeExpertiseBanner.IsAtTop(readme, banner))
		{
			return Task.FromResult(Pass("README.md opens with the expertise statement."));
		}

		return Task.FromResult(Fail(
			"README.md does not open with the expertise statement.",
			new RuleAdvisory
			{
				Summary = "Put the standard expertise statement directly under the title and badges at the top of README.md.",
				Detail = $"Place this line directly after the title and any badges, before the first section, and nowhere else: `{banner}`",
				Data = new()
				{
					["file"] = "README.md",
					["remediation_type"] = "insert_readme_banner",
					["banner"] = banner
				}
			}));
	}
}
