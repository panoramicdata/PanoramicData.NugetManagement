using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that CLAUDE.md does not name CONTRIBUTING.md in the two places the earlier template did.
/// </summary>
/// <remarks>
/// Codacy's Agentlinter treats a file name in an instruction file as a reference and reports
/// "Referenced file CONTRIBUTING.md not found in workspace" when its workspace holds no such file —
/// which it does not, whether or not the repository has one. That is a High ErrorProne issue and
/// grades the file C, and the v1.2 template named it twice, so every repository generated from it
/// carried the finding. Only those two phrasings are rewritten: a mention a person wrote is theirs,
/// and flagging it would leave a Fix button that changes nothing.
/// </remarks>
public class ClaudeMdNamesNoUnresolvedFileRule : RuleBase
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
	public override string RuleName => "CLAUDE.md names no file Codacy cannot find";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.AI;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("CLAUDE.md");
		if (content is null)
		{
			return Task.FromResult(NotApplicable("No CLAUDE.md to check; AI-01 reports the missing file."));
		}

		var present = _rewrites
			.Where(rewrite => Regex.IsMatch(content, rewrite.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
			.ToList();

		if (present.Count == 0)
		{
			return Task.FromResult(Pass("CLAUDE.md names no file that Codacy's reference check cannot resolve."));
		}

		return Task.FromResult(Fail(
			"CLAUDE.md names CONTRIBUTING.md, which Codacy reports as a missing file reference and grades the file C.",
			new RuleAdvisory
			{
				Summary = "Reword the CONTRIBUTING.md mentions in CLAUDE.md so Codacy sees no file reference.",
				Detail = "Codacy's Agentlinter reports `Referenced file \"CONTRIBUTING.md\" not found in workspace` "
					+ "(High, ErrorProne) for a file name in an instruction file, whether or not the repository has "
					+ "that file. Reword the mention so no file is named — the current template says "
					+ "\"following the repository's contributing guidelines\" — and leave the rest of the file as it is.",
				Data = new()
				{
					["remediation_type"] = "replace_regex_in_file",
					["file"] = "CLAUDE.md",
					["patterns"] = present.Select(rewrite => rewrite.Pattern).ToArray(),
					["replacements"] = present.Select(rewrite => rewrite.Replacement).ToArray()
				}
			}));
	}
}
