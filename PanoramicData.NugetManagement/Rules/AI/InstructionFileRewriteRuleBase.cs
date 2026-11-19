using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// A rule over CLAUDE.md and AGENTS.md whose only fix is a case-sensitive rewrite of phrasings an earlier
/// template used, so it reports exactly what its remediation can change.
/// </summary>
public abstract class InstructionFileRewriteRuleBase : RuleBase
{
	private static readonly string[] _files = ["CLAUDE.md", "AGENTS.md"];

	/// <summary>Gets each phrasing the earlier template used, paired with what replaces it.</summary>
	protected abstract IReadOnlyList<(string Pattern, string Replacement)> Rewrites { get; }

	/// <summary>Gets the message when no file needs rewording.</summary>
	protected abstract string PassMessage { get; }

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
			.Select(entry => (entry.File, Rewrites: Rewrites
				.Where(rewrite => Regex.IsMatch(entry.Content!, rewrite.Pattern, RegexOptions.None, TimeSpan.FromSeconds(2)))
				.ToList()))
			.Where(entry => entry.Rewrites.Count > 0)
			.ToList();

		if (affected.Count == 0)
		{
			return Task.FromResult(Pass(PassMessage));
		}

		// One pattern list for every file: a pattern a file does not contain is a no-op there.
		var rewrites = affected.SelectMany(entry => entry.Rewrites).Distinct().ToList();
		var names = string.Join(" and ", affected.Select(entry => entry.File));

		return Task.FromResult(Fail(
			FailureMessage(names, affected.Count == 1 ? "s" : string.Empty),
			new RuleAdvisory
			{
				Summary = Summary(names),
				Detail = Detail,
				Data = new()
				{
					["remediation_type"] = "replace_regex_in_files",
					["globs"] = affected.Select(entry => entry.File).ToArray(),
					["patterns"] = rewrites.Select(rewrite => rewrite.Pattern).ToArray(),
					["replacements"] = rewrites.Select(rewrite => rewrite.Replacement).ToArray()
				}
			}));
	}

	/// <summary>Builds the failure message.</summary>
	/// <param name="names">The files that need rewording, joined with "and".</param>
	/// <param name="verbSuffix">"s" when there is one file, otherwise empty, for the verb that follows the names.</param>
	protected abstract string FailureMessage(string names, string verbSuffix);

	/// <summary>Builds the one-line advice.</summary>
	/// <param name="names">The files that need rewording, joined with "and".</param>
	protected abstract string Summary(string names);

	/// <summary>Gets the explanation of what is rewritten and why.</summary>
	protected abstract string Detail { get; }
}
