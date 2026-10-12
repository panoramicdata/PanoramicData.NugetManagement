namespace PanoramicData.NugetManagement.Web.Remediations.AI;

/// <summary>Rewords the em dash and the heading capitalisation in the generated CLAUDE.md and AGENTS.md.</summary>
public sealed class InstructionFileWordingRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "AI-05";
}
