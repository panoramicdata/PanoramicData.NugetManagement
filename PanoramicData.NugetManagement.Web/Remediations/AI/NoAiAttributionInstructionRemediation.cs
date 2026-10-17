namespace PanoramicData.NugetManagement.Web.Remediations.AI;

/// <summary>Adds the instruction not to add AI attribution to commits to CLAUDE.md and AGENTS.md.</summary>
public sealed class NoAiAttributionInstructionRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "AI-06";
}
