using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that AGENTS.md exists and, unless PanoramicData.Skills manages it, is standalone: it must not
/// describe Panoramic Data's internal tooling in a repository that may be public.
/// </summary>
/// <remarks>
/// A file carrying the Skills managed block belongs to that repository's own audit and passes. A file
/// this tool generated earlier is replaced; a hand-written file with a leak fails with no fix, because
/// only a person can tell which of its lines are theirs.
/// </remarks>
public class AgentsMdStandardRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "AI-02";

	/// <inheritdoc />
	public override string RuleName => "AGENTS.md follows the instructions standard";

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
					Summary = "Create AGENTS.md from the standard public template",
					Detail = "AGENTS.md is the one instructions file that Claude Code, Codex and Copilot all read. The template is standalone and mentions Panoramic Data only in a generic staff section.",
					Data = new()
					{
						["expected_path"] = "AGENTS.md",
						["template_content"] = Standards.AgentsMdContent
					}
				}));
		}

		if (Standards.HasManagedBlock(content))
		{
			return Task.FromResult(Pass("AGENTS.md carries the PanoramicData.Skills managed block, which that repository audits."));
		}

		if (Standards.IsGeneratedByThisTool(content, Standards.PreviousAgentsMdContent, Standards.LegacyAgentsMdContent, Standards.PreviousStandaloneAgentsMdContent))
		{
			return Task.FromResult(Fail(
				"AGENTS.md is an earlier generated template that has since been replaced.",
				new RuleAdvisory
				{
					Summary = "Replace the generated AGENTS.md with the standalone template",
					Detail = "The file is only what this tool generated, so it is replaced; a hand-written file is never touched.",
					Data = new()
					{
						["remediation_type"] = "replace_file_content",
						["file"] = "AGENTS.md",
						["new_content"] = Standards.AgentsMdContent
					}
				}));
		}

		var leaks = Standards.FindPublicAgentsMdLeaks(content);
		return Task.FromResult(leaks.Count == 0
			? Pass("AGENTS.md is standalone.")
			: Fail(
				"AGENTS.md " + string.Join(" and ", leaks) + ".",
				new RuleAdvisory
				{
					Summary = "Remove the internal tooling references from AGENTS.md",
					Detail = "A public repository must not describe Panoramic Data's internal tooling. Keep the generic staff section, and remove the rest by hand: the file holds text this tool did not generate."
				}));
	}
}
