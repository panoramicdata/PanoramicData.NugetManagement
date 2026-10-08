using System.Text.RegularExpressions;
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
public class ClaudeMdNamesNoUnresolvedFileRule : RuleBase
{
	private static readonly string[] _files = ["CLAUDE.md", "AGENTS.md"];

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
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var present = _files
			.Select(file => (File: file, Content: context.GetFileContent(file)))
			.Where(entry => entry.Content is not null)
			.ToList();

		if (present.Count == 0)
		{
			return Task.FromResult(NotApplicable("No CLAUDE.md or AGENTS.md to check; AI-01 and AI-02 report the missing files."));
		}

		var affected = present
			.Select(entry => (entry.File, Rewrites: _rewrites
				.Where(rewrite => Regex.IsMatch(entry.Content!, rewrite.Pattern, RegexOptions.None, TimeSpan.FromSeconds(2)))
				.ToList()))
			.Where(entry => entry.Rewrites.Count > 0)
			.ToList();

		if (affected.Count == 0)
		{
			return Task.FromResult(Pass("No instruction file names a file that Codacy's reference check cannot resolve."));
		}

		// One pattern list for every file: a pattern a file does not contain is a no-op there.
		var rewrites = affected.SelectMany(entry => entry.Rewrites).Distinct().ToList();
		var names = string.Join(" and ", affected.Select(entry => entry.File));

		return Task.FromResult(Fail(
			$"{names} name{(affected.Count == 1 ? "s" : string.Empty)} CONTRIBUTING.md, which Codacy reports as a missing file reference.",
			new RuleAdvisory
			{
				Summary = $"Reword the CONTRIBUTING.md mentions in {names} so Codacy sees no file reference.",
				Detail = "Codacy's Agentlinter reports `Referenced file \"CONTRIBUTING.md\" not found in workspace` "
					+ "(High, ErrorProne) for a file name in an instruction file, whether or not the repository has "
					+ "that file. Reword the mention so no file is named — the current template says "
					+ "\"following the repository's contributing guidelines\" — and leave the rest of the file as it is.",
				Data = new()
				{
					["remediation_type"] = "replace_regex_in_files",
					["globs"] = affected.Select(entry => entry.File).ToArray(),
					["patterns"] = rewrites.Select(rewrite => rewrite.Pattern).ToArray(),
					["replacements"] = rewrites.Select(rewrite => rewrite.Replacement).ToArray()
				}
			}));
	}
}
