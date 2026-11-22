using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that AGENTS.md tells agents not to add AI attribution to commits and pull requests.
/// </summary>
/// <remarks>
/// Agents add a <c>Co-Authored-By</c> trailer to every commit unless told not to, and Panoramic Data does
/// not want them in its history. A file carrying the PanoramicData.Skills managed block is that
/// repository's own to audit.
/// </remarks>
public class NoAiAttributionInstructionRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "AI-06";

	/// <inheritdoc />
	public override string RuleName => "AGENTS.md forbids AI attribution in commits";

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
			return Task.FromResult(NotApplicable("No AGENTS.md to check; AI-02 reports the missing file."));
		}

		if (Standards.HasManagedBlock(content))
		{
			return Task.FromResult(NotApplicable("AGENTS.md carries the PanoramicData.Skills managed block, which that repository audits."));
		}

		return Task.FromResult(content.Contains(Standards.NoAiAttributionInstruction, StringComparison.Ordinal)
			? Pass("AGENTS.md tells agents not to add AI attribution to commits.")
			: Fail(
				"AGENTS.md does not tell agents to leave `Co-Authored-By` trailers out of commits.",
				new RuleAdvisory
				{
					Summary = "Tell agents not to add AI attribution to commits and pull requests.",
					Detail = "Add this line to AGENTS.md: \"" + Standards.NoAiAttributionInstruction + "\"",
					Data = new()
					{
						["remediation_type"] = "append_line",
						["file"] = "AGENTS.md",
						["line_content"] = Standards.NoAiAttributionInstruction
					}
				}));
	}
}
