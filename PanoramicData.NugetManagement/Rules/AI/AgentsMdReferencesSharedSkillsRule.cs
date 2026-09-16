using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that AGENTS.md exists and references the shared Panoramic Data skills.
/// </summary>
public class AgentsMdReferencesSharedSkillsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "AI-02";

	/// <inheritdoc />
	public override string RuleName => "AGENTS.md references shared skills";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.AI;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("AGENTS.md");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"AGENTS.md not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create AGENTS.md referencing the shared Panoramic Data skills",
					Detail = "Create an `AGENTS.md` file at the repository root that references the shared Panoramic Data skills, worded so agents without access to the private repository skip it rather than stop.",
					Data = new()
					{
						["expected_path"] = "AGENTS.md",
						["template_content"] = Standards.AgentsMdContent
					}
				}));
		}

		return Task.FromResult(Contains(content, Standards.SharedSkillsCopilotInstructionsPath)
			? Pass("AGENTS.md found and references the shared Panoramic Data skills.")
			: Fail(
				"AGENTS.md does not reference the shared Panoramic Data skills.",
				new RuleAdvisory
				{
					Summary = "Add a reference to the shared Panoramic Data skills to AGENTS.md.",
					Detail = "Add a line referencing `" + Standards.SharedSkillsCopilotInstructionsPath + "` to `AGENTS.md`, worded so agents without access to the private `PanoramicData.Skills` repository skip it rather than stop.",
					Data = new()
					{
						["remediation_type"] = "append_line",
						["file"] = "AGENTS.md",
						["line_content"] = Standards.SharedSkillsCopilotInstructionsPath
					}
				}));
	}
}
