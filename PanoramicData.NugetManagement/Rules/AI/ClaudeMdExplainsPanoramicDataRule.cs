using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that CLAUDE.md says who Panoramic Data is and how the repository is governed.
/// </summary>
/// <remarks>
/// Codacy's Agentlinter grades an instruction file with no identity as F, and an agent with no
/// context about who publishes the repository, or why files like this one appear in it, has to guess.
/// The text is deliberately generic: it names no people, systems or customers.
/// </remarks>
public class ClaudeMdExplainsPanoramicDataRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "AI-03";

	/// <inheritdoc />
	public override string RuleName => "CLAUDE.md explains Panoramic Data";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.AI;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("CLAUDE.md");
		if (content is null)
		{
			return Task.FromResult(NotApplicable("No CLAUDE.md to describe Panoramic Data in; AI-01 reports the missing file."));
		}

		return Task.FromResult(Contains(content, Standards.GovernanceToolName)
			? Pass("CLAUDE.md explains Panoramic Data and how the repository is governed.")
			: Fail(
				"CLAUDE.md does not say who Panoramic Data is or how this repository is governed.",
				new RuleAdvisory
				{
					Summary = "Add the standard About Panoramic Data section to CLAUDE.md.",
					Detail = "Add the `" + Standards.AboutSectionHeading + "` section: who publishes the package and that PanoramicData.NugetManagement governs it.",
					Data = new()
					{
						["remediation_type"] = "append_line",
						["file"] = "CLAUDE.md",
						["line_content"] = Standards.AboutSectionContent
					}
				}));
	}
}
