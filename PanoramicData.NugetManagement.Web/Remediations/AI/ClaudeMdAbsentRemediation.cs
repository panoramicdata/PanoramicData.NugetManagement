namespace PanoramicData.NugetManagement.Web.Remediations.AI;

/// <summary>Deletes a CLAUDE.md that this tool generated.</summary>
public sealed class ClaudeMdAbsentRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "AI-01";
}
