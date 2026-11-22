using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that there is no CLAUDE.md: AGENTS.md is the single instructions file, and while a CLAUDE.md
/// exists Claude Code ignores AGENTS.md.
/// </summary>
/// <remarks>
/// Only a file this tool generated is deleted. One with anyone else's text in it fails with no fix, so
/// that text is moved into AGENTS.md by hand instead of being lost.
/// </remarks>
public class ClaudeMdAbsentRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "AI-01";

	/// <inheritdoc />
	public override string RuleName => "No CLAUDE.md (AGENTS.md is the instructions file)";

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
			return Task.FromResult(Pass("No CLAUDE.md; AGENTS.md is the single instructions file."));
		}

		const string message = "CLAUDE.md exists, so Claude Code ignores AGENTS.md.";
		if (!Standards.IsGeneratedByThisTool(content, Standards.PreviousClaudeMdContent, Standards.LegacyClaudeMdContent))
		{
			return Task.FromResult(Fail(
				message,
				new RuleAdvisory
				{
					Summary = "Move CLAUDE.md's own text into AGENTS.md, then delete CLAUDE.md.",
					Detail = "CLAUDE.md holds text this tool did not generate, so it is not deleted automatically."
				}));
		}

		return Task.FromResult(Fail(
			message,
			new RuleAdvisory
			{
				Summary = "Delete the generated CLAUDE.md.",
				Detail = "The file is only what this tool generated, so AGENTS.md carries everything it said.",
				Data = new()
				{
					["remediation_type"] = "delete_file",
					["file"] = "CLAUDE.md"
				}
			}));
	}
}
