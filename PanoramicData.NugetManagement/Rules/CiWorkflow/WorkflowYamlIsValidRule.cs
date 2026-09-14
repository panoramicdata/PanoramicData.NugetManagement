using PanoramicData.NugetManagement.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Every GitHub Actions workflow file parses as YAML.
/// </summary>
/// <remarks>
/// Critical, and the only rule here that reads a workflow as YAML rather than as text. A workflow
/// GitHub cannot parse is rejected before any step runs: the run appears, fails in zero seconds, and
/// reports "this run likely failed because of a workflow file issue" — so the repository looks like
/// it has CI, and the checks look like they run, and neither is true.
/// <para>
/// It exists because that happened. A governance remediation rewrote the indentation of about thirty
/// repositories' workflow files to tabs, which YAML forbids for indentation. CodeQL stopped
/// analysing all of them and CI stopped running on four, for weeks, and every other rule here kept
/// passing because they all read workflows with string matching — which is perfectly happy with a
/// file that will never execute.
/// </para>
/// </remarks>
public class WorkflowYamlIsValidRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-14";

	/// <inheritdoc />
	public override string RuleName => "Workflow files are valid YAML";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <summary>
	/// Critical: an unparseable workflow silently disables whatever it was meant to run.
	/// </summary>
	public override AssessmentSeverity Severity => AssessmentSeverity.Critical;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var workflows = context.FilePaths
			.Where(IsWorkflowFile)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (workflows.Count == 0)
		{
			return Task.FromResult(NotApplicable("No workflow files found."));
		}

		var broken = new List<(string Path, string Error)>();
		var read = 0;

		foreach (var path in workflows)
		{
			// Null means the file was listed but never fetched, which says nothing about its contents.
			// Reporting an unread file as invalid would fail every repository the fetcher skipped.
			var content = context.GetFileContent(path);
			if (content is null)
			{
				continue;
			}

			read++;

			if (TryDescribeParseFailure(content, out var error))
			{
				broken.Add((path, error));
			}
		}

		if (read == 0)
		{
			return Task.FromResult(NotApplicable("No workflow file contents were read."));
		}

		if (broken.Count == 0)
		{
			return Task.FromResult(Pass($"All {read} workflow file(s) parse as YAML."));
		}

		var names = string.Join(", ", broken.Select(b => Path.GetFileName(b.Path)));
		var detail = string.Join("\n", broken.Select(b => $"  {b.Path}: {b.Error}"));

		return Task.FromResult(Fail(
			$"{broken.Count} workflow file(s) do not parse as YAML: {names}.",
			new RuleAdvisory
			{
				Summary = "Fix the workflow YAML so GitHub can run it.",
				Detail = $"""
					These workflow files are not valid YAML, so GitHub rejects them before any step
					runs. The run still appears in the Actions tab and still fails, in zero seconds,
					which is why this can go unnoticed for a long time: the repository looks like it
					has working CI.

					{detail}

					The usual cause is indentation rewritten to tab characters. YAML forbids tabs for
					indentation anywhere, whatever the repository's convention is for source files.
					Replace each leading tab with spaces — in these templates a tab stands for four —
					and leave tabs inside quoted values alone.
					""",
				Data = new()
				{
					["broken_count"] = broken.Count,
					["broken_files"] = broken.Select(b => b.Path).ToList()
				}
			}));
	}

	private static bool IsWorkflowFile(string path)
		=> path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
			&& (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Whether the content fails to parse, and what the parser said when it did.
	/// </summary>
	private static bool TryDescribeParseFailure(string content, out string error)
	{
		try
		{
			var stream = new YamlStream();
			stream.Load(new StringReader(content));
			error = string.Empty;
			return false;
		}
		catch (YamlException ex)
		{
			// The message alone rarely says where. The line number is what makes this actionable.
			error = $"line {ex.Start.Line}: {ex.Message}";
			return true;
		}
	}
}
