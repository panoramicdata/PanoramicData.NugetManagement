namespace PanoramicData.NugetManagement.Web.Remediations.AI;

/// <summary>Creates or fixes CLAUDE.md so it imports the shared Panoramic Data skills.</summary>
public sealed class ClaudeMdReferencesSharedSkillsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "AI-01";
}
