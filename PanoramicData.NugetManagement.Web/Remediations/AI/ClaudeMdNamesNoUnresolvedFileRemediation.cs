namespace PanoramicData.NugetManagement.Web.Remediations.AI;

/// <summary>Rewords the CONTRIBUTING.md mentions in CLAUDE.md that Codacy reports as missing files.</summary>
public sealed class ClaudeMdNamesNoUnresolvedFileRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "AI-04";
}
