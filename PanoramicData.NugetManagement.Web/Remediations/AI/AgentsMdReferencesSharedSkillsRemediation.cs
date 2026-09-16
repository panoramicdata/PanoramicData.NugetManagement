namespace PanoramicData.NugetManagement.Web.Remediations.AI;

/// <summary>Creates or fixes AGENTS.md so it references the shared Panoramic Data skills.</summary>
public sealed class AgentsMdReferencesSharedSkillsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "AI-02";
}
