using System.Text.RegularExpressions;
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
public class InstructionFileWordingRule : RuleBase
{
	private static readonly string[] _files = ["CLAUDE.md", "AGENTS.md"];

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
			return Task.FromResult(Pass("Instruction files follow the wording standard."));
		}

		// One pattern list for every file: a pattern a file does not contain is a no-op there.
		var rewrites = affected.SelectMany(entry => entry.Rewrites).Distinct().ToList();
		var names = string.Join(" and ", affected.Select(entry => entry.File));

		return Task.FromResult(Fail(
			$"{names} break{(affected.Count == 1 ? "s" : string.Empty)} the wording standard: an em dash, or a heading that capitalises more than its first word.",
			new RuleAdvisory
			{
				Summary = $"Reword {names} to the wording standard.",
				Detail = "The shared Panoramic Data instructions forbid an em dash in any generated text and ask that "
					+ "a heading capitalise its first word only. The earlier template used both once. Reword those "
					+ "two places as the current template does and leave the rest of the file as it is.",
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
