using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that CLAUDE.md and AGENTS.md do not name CONTRIBUTING.md in the places the earlier template did.
/// </summary>
/// <remarks>
/// Codacy's Agentlinter treats a file name in an instruction file as a reference and reports
/// "Referenced file CONTRIBUTING.md not found in workspace" when its workspace holds no such file —
/// which it does not, whether or not the repository has one. That is a High ErrorProne issue and
/// grades the file C, and the v1.2 template named it twice, so every repository generated from it
/// carried the finding in CLAUDE.md. AGENTS.md shares the About paragraph and so names it once; it has
/// not been reported, but it is the same reference and is rewritten to the same text. Only those
/// phrasings are rewritten: a mention a person wrote is theirs, and flagging it would leave a Fix
/// button that changes nothing.
/// <para>
/// Matching is case-sensitive on purpose, because the remediation's multi-file replace is: a rule that
/// flagged text the fix then could not change would never clear.
/// </para>
/// </remarks>
public class ClaudeMdNamesNoUnresolvedFileRule : InstructionFileRewriteRuleBase
{
	/// <summary>
	/// Each phrasing the earlier template used, paired with what replaces it. The replacements are the
	/// current template's wording, so a fixed file lands exactly on what a new repository gets.
	/// </summary>
	private static readonly (string Pattern, string Replacement)[] _rewrites =
	[
		(@"AGENTS\.md, SECURITY\.md and CONTRIBUTING\.md", "AGENTS.md and SECURITY.md"),
		(@"following `CONTRIBUTING\.md` where present", "following the repository's contributing guidelines where present")
	];

	/// <inheritdoc />
	public override string RuleId => "AI-04";

	/// <inheritdoc />
	public override string RuleName => "Instruction files name no file Codacy cannot find";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.AI;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	protected override IReadOnlyList<(string Pattern, string Replacement)> Rewrites => _rewrites;

	/// <inheritdoc />
	protected override string PassMessage => "No instruction file names a file that Codacy's reference check cannot resolve.";

	/// <inheritdoc />
	protected override string Detail
		=> "Codacy's Agentlinter reports `Referenced file \"CONTRIBUTING.md\" not found in workspace` "
			+ "(High, ErrorProne) for a file name in an instruction file, whether or not the repository has "
			+ "that file. Reword the mention so no file is named — the current template says "
			+ "\"following the repository's contributing guidelines\" — and leave the rest of the file as it is.";

	/// <inheritdoc />
	protected override string FailureMessage(string names, string verbSuffix)
		=> $"{names} name{verbSuffix} CONTRIBUTING.md, which Codacy reports as a missing file reference.";

	/// <inheritdoc />
	protected override string Summary(string names) => $"Reword the CONTRIBUTING.md mentions in {names} so Codacy sees no file reference.";
}
