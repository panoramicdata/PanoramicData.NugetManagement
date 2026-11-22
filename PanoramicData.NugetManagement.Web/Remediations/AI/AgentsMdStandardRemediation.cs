namespace PanoramicData.NugetManagement.Web.Remediations.AI;

/// <summary>Creates AGENTS.md from the standard template, or replaces a generated earlier one.</summary>
public sealed class AgentsMdStandardRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "AI-02";
}
