using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that CLAUDE.md exists and references the shared Panoramic Data skills.
/// </summary>
public class ClaudeMdReferencesSharedSkillsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "AI-01";

	/// <inheritdoc />
	public override string RuleName => "CLAUDE.md references shared skills";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.AI;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("CLAUDE.md");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"CLAUDE.md not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create CLAUDE.md importing the shared Panoramic Data skills",
					Detail = "Create a `CLAUDE.md` file at the repository root that imports local Copilot instructions and the shared Panoramic Data skills.",
					Data = new()
					{
						["expected_path"] = "CLAUDE.md",
						["template_content"] = Standards.ClaudeMdContent
					}
				}));
		}

		return Task.FromResult(Contains(content, Standards.SharedSkillsCopilotInstructionsPath)
			? Pass("CLAUDE.md found and references the shared Panoramic Data skills.")
			: Fail(
				"CLAUDE.md does not reference the shared Panoramic Data skills.",
				new RuleAdvisory
				{
					Summary = "Add an @import of the shared Panoramic Data skills to CLAUDE.md.",
					Detail = "Add `@" + Standards.SharedSkillsCopilotInstructionsPath + "` as a line in `CLAUDE.md`. Claude Code skips an `@import` whose path does not resolve, so contributors without access to the private `PanoramicData.Skills` repository are unaffected.",
					Data = new()
					{
						["remediation_type"] = "append_line",
						["file"] = "CLAUDE.md",
						["line_content"] = "@" + Standards.SharedSkillsCopilotInstructionsPath
					}
				}));
	}
}
