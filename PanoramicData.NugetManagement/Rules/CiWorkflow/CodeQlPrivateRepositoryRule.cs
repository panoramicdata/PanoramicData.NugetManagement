using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a CodeQL workflow in a private repository does not run where it cannot upload its results.
/// </summary>
/// <remarks>
/// Code scanning in a private repository needs GitHub Advanced Security. Without it the analysis runs
/// and then fails at "Uploading code scanning results", so the repository is red on every push for a
/// reason no code change can fix. Guarding the job on the repository being public keeps COM-04's workflow
/// in place and the check green; it scans again on its own where scanning is available.
/// </remarks>
public partial class CodeQlPrivateRepositoryRule : RuleBase
{
	private const string _guard = "github.event.repository.private";

	/// <inheritdoc />
	public override string RuleId => "CI-18";

	/// <inheritdoc />
	public override string RuleName => "CodeQL is skipped in private repositories";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (context.IsPrivateRepository is not { } isPrivate)
		{
			return Task.FromResult(NotApplicable("Whether the repository is private could not be read, so it is neither reported nor assumed."));
		}

		if (!isPrivate)
		{
			return Task.FromResult(Pass("The repository is public, so code scanning is available."));
		}

		var unguarded = new List<string>();
		string? fixable = null;
		foreach (var path in context.FilePaths.Where(IsWorkflow))
		{
			var content = context.GetFileContent(path);
			if (content is null || !content.Contains("github/codeql-action", StringComparison.OrdinalIgnoreCase) || content.Contains(_guard, StringComparison.Ordinal))
			{
				continue;
			}

			unguarded.Add(path);
			fixable ??= path;
			if (SingleJob(content) is null)
			{
				fixable = null;
			}
		}

		if (unguarded.Count == 0)
		{
			return Task.FromResult(Pass("No CodeQL workflow runs in this private repository."));
		}

		var data = new Dictionary<string, object> { ["workflows"] = unguarded.ToArray() };
		if (fixable is not null && unguarded.Count == 1)
		{
			data["remediation_type"] = "replace_regex_in_file";
			data["file"] = fixable;
			data["patterns"] = new[] { _jobPattern };
			data["replacements"] = new[] { _jobReplacement };
		}

		return Task.FromResult(Fail(
			$"{string.Join(", ", unguarded)} runs CodeQL in a private repository, where uploading results needs GitHub Advanced Security and fails without it.",
			new RuleAdvisory
			{
				Summary = $"Guard the CodeQL job with `if: ${{{{ !{_guard} }}}}`.",
				Detail = $"Add `if: ${{{{ !{_guard} }}}}` to the job in {string.Join(", ", unguarded)}. Code scanning in a private repository needs GitHub Advanced Security; without it every run fails after the analysis, at the upload step.",
				Data = data
			}));
	}

	private const string _jobPattern = @"(?m)^(jobs:[ \t]*)(\r?\n)([ \t]+)([\w-]+):[ \t]*(\r?\n)";

	// $$ is a literal dollar, so the inserted line reads ${{ !github.event.repository.private }}.
	private const string _jobReplacement = "${1}${2}${3}${4}:${5}${3}${3}if: $${{ !github.event.repository.private }}${5}";

	[GeneratedRegex(_jobPattern)]
	private static partial Regex FirstJob();

	/// <summary>
	/// The job key when the workflow has exactly one job, otherwise null: a second job would be left
	/// unguarded, so that case is not rewritten mechanically.
	/// </summary>
	private static string? SingleJob(string content)
	{
		var first = FirstJob().Match(content);
		if (!first.Success)
		{
			return null;
		}

		var indent = first.Groups[3].Value;
		var jobs = content[first.Index..].Replace("\r\n", "\n").Split('\n')
			.Skip(1)
			.TakeWhile(line => line.Length == 0 || char.IsWhiteSpace(line[0]) || line.TrimStart().StartsWith('#'))
			.Count(line => line.StartsWith(indent, StringComparison.Ordinal)
				&& line.Length > indent.Length
				&& !char.IsWhiteSpace(line[indent.Length])
				&& line.TrimEnd().EndsWith(':'));

		return jobs == 1 ? first.Groups[4].Value : null;
	}

	private static bool IsWorkflow(string path)
		=> path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
			&& (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
}
