using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that CLAUDE.md and AGENTS.md tell agents not to add <c>Co-Authored-By</c> trailers or other
/// AI attribution to commits and pull requests.
/// </summary>
/// <remarks>
/// Agents add a co-author trailer to every commit unless an instruction says otherwise, and Panoramic
/// Data does not want them in its history. The rule looks for the exact instruction, not the word
/// <c>Co-Authored-By</c>, because a file that names the trailer to ask for it must not pass.
/// <para>
/// The fix places the line where the v1.3 template has it, anchored on v1.2 template text, and moves
/// the version marker to 1.3 so a fixed file lands exactly on what a new repository gets. A file that
/// lacks the anchor still fails, but offers no fix: a Fix button that changes nothing is worse than none.
/// </para>
/// </remarks>
public class NoAiAttributionInstructionRule : RuleBase
{
	private const string _version = @"(?m)^Version: 1\.2(?=\r?$)";

	/// <summary>
	/// Per file: the v1.2 template text the instruction goes next to, and what replaces it. The
	/// captured line ending is reused so a CRLF file stays CRLF.
	/// </summary>
	private static readonly Dictionary<string, (string Pattern, string Replacement)> _anchors = new()
	{
		["CLAUDE.md"] = (
			@"(delete branches without explicit\r?\n  approval\.)(\r?\n)",
			"$1$2- " + Standards.NoAiAttributionInstruction + "$2"),
		["AGENTS.md"] = (
			@"(\r?\n)(the repository to be cloned\.)",
			"$1$2$1$1## Commits$1$1" + Standards.NoAiAttributionInstruction)
	};

	/// <inheritdoc />
	public override string RuleId => "AI-06";

	/// <inheritdoc />
	public override string RuleName => "Instruction files forbid AI attribution in commits";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.AI;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var missing = _anchors.Keys
			.Select(file => (File: file, Content: context.GetFileContent(file)))
			.Where(entry => entry.Content is not null)
			.ToList();

		if (missing.Count == 0)
		{
			return Task.FromResult(NotApplicable("No CLAUDE.md or AGENTS.md to check; AI-01 and AI-02 report the missing files."));
		}

		missing = [.. missing.Where(entry => !entry.Content!.Contains(Standards.NoAiAttributionInstruction, StringComparison.Ordinal))];

		if (missing.Count == 0)
		{
			return Task.FromResult(Pass("Instruction files tell agents not to add AI attribution to commits."));
		}

		var names = string.Join(" and ", missing.Select(entry => entry.File));
		var message = $"{names} do{(missing.Count == 1 ? "es" : string.Empty)} not tell agents to leave `Co-Authored-By` trailers out of commits.";
		const string summary = "Tell agents not to add AI attribution to commits and pull requests.";
		var detail = "Agents add a `Co-Authored-By` trailer to every commit unless told not to. Add this line to "
			+ names + ": \"" + Standards.NoAiAttributionInstruction + "\"";

		var fixable = missing.All(entry => Regex.IsMatch(entry.Content!, _anchors[entry.File].Pattern, RegexOptions.None, TimeSpan.FromSeconds(2)));
		if (!fixable)
		{
			return Task.FromResult(Fail(message, new RuleAdvisory { Summary = summary, Detail = detail }));
		}

		// One pattern list for every file: a pattern a file does not contain is a no-op there.
		var rewrites = missing.Select(entry => _anchors[entry.File]).Append((_version, "Version: " + Standards.AiInstructionsTemplateVersion)).ToList();

		return Task.FromResult(Fail(
			message,
			new RuleAdvisory
			{
				Summary = summary,
				Detail = detail,
				Data = new()
				{
					["remediation_type"] = "replace_regex_in_files",
					["globs"] = missing.Select(entry => entry.File).ToArray(),
					["patterns"] = rewrites.Select(rewrite => rewrite.Item1).ToArray(),
					["replacements"] = rewrites.Select(rewrite => rewrite.Item2).ToArray()
				}
			}));
	}
}
