using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the generated CLAUDE.md and AGENTS.md follow the wording standard the shared Skills
/// instructions set for all generated text.
/// </summary>
/// <remarks>
/// The standard is two rules: never an em dash, and a heading capitalises its first word only. The
/// AGENTS.md template broke both, once each, and so did every copy of it in the estate. Only those
/// phrasings are rewritten. A sentence someone wrote themselves is theirs, and flagging it would leave
/// a Fix button that changes nothing, so this rule reports only what its fix can change.
/// <para>
/// Matching is case-sensitive on purpose, because the remediation's multi-file replace is.
/// </para>
/// </remarks>
public class InstructionFileWordingRule : InstructionFileRewriteRuleBase
{
	/// <summary>
	/// Each phrasing the earlier template used, paired with the current template's wording, so a fixed
	/// file lands exactly on what a new repository gets.
	/// </summary>
	/// <remarks>
	/// The em dash is written as a regex escape so this file contains none itself. The heading pattern
	/// ends in a lookahead rather than <c>$</c> alone because a CRLF file leaves a carriage return
	/// between the text and the line end.
	/// </remarks>
	private static readonly (string Pattern, string Replacement)[] _rewrites =
	[
		(@"(?m)^# Agent Instructions(?=\r?$)", "# Agent instructions"),
		(@"read it before starting work — it documents", "read it before starting work, as it documents")
	];

	/// <inheritdoc />
	public override string RuleId => "AI-05";

	/// <inheritdoc />
	public override string RuleName => "Instruction files follow the wording standard";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.AI;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	protected override IReadOnlyList<(string Pattern, string Replacement)> Rewrites => _rewrites;

	/// <inheritdoc />
	protected override string PassMessage => "Instruction files follow the wording standard.";

	/// <inheritdoc />
	protected override string Detail
		=> "The shared Panoramic Data instructions forbid an em dash in any generated text and ask that "
			+ "a heading capitalise its first word only. The earlier template used both once. Reword those "
			+ "two places as the current template does and leave the rest of the file as it is.";

	/// <inheritdoc />
	protected override string FailureMessage(string names, string verbSuffix)
		=> $"{names} break{verbSuffix} the wording standard: an em dash, or a heading that capitalises more than its first word.";

	/// <inheritdoc />
	protected override string Summary(string names) => $"Reword {names} to the wording standard.";
}
