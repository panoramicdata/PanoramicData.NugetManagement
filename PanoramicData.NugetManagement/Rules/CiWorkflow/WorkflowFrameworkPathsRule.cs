using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a workflow's build-output paths name a framework the repository still targets.
/// </summary>
public partial class WorkflowFrameworkPathsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-17";

	/// <inheritdoc />
	public override string RuleName => "CI paths name a framework the repository targets";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <summary>
	/// A framework folder under a build configuration, as in <c>bin/Debug/net10.0/</c>.
	/// </summary>
	[GeneratedRegex(@"(?<=[/\\](?:Debug|Release)[/\\])net\d+\.\d+(?:-[A-Za-z0-9.]+)?(?=[/\\\s'""]|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
	private static partial Regex FrameworkFolder();

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var targeted = TargetedFrameworks(context);
		var latest = Standards.LatestTargetFramework;

		// Until the repository is on the latest framework its paths are right as they are: TFM-01 moves the
		// projects first, and this rule follows once they have moved.
		if (!targeted.Contains(latest))
		{
			return Task.FromResult(Pass($"The repository does not target {latest} yet, so its workflow paths are not stale."));
		}

		var stale = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		var files = new List<string>();

		foreach (var path in context.FilePaths.Where(IsWorkflow))
		{
			var found = FrameworkFolder().Matches(context.GetFileContent(path) ?? string.Empty)
				.Select(match => match.Value)
				.Where(framework => !targeted.Contains(framework))
				.ToList();

			if (found.Count > 0)
			{
				files.Add(path);
				stale.UnionWith(found);
			}
		}

		if (stale.Count == 0)
		{
			return Task.FromResult(Pass("Every framework named in a workflow's build-output path is one the repository targets."));
		}

		var list = string.Join(", ", stale.Select(framework => $"`{framework}`"));
		return Task.FromResult(Fail(
			$"Workflow paths name {list}, which no project targets, so the step that uses them cannot find the build output: {string.Join(", ", files)}.",
			new RuleAdvisory
			{
				Summary = $"Change the framework in the workflow's bin/<configuration>/ paths to {latest}.",
				Detail = $"The repository targets {latest} but {string.Join(", ", files)} still point at {list} under `bin/<configuration>/`. The build output is in the {latest} folder, so a step that runs a test project or reads coverage from the old folder fails and takes the release run with it.",
				Data = new()
				{
					["remediation_type"] = "replace_regex_in_files",
					["globs"] = files.ToArray(),
					["patterns"] = stale.Select(framework => $@"(?<=[/\\](?:Debug|Release)[/\\]){Regex.Escape(framework)}(?=[/\\\s'""]|$)").ToArray(),
					["replacements"] = stale.Select(_ => latest).ToArray()
				}
			}));
	}

	private static bool IsWorkflow(string path)
		=> path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
			&& (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

	private static HashSet<string> TargetedFrameworks(RepositoryContext context)
	{
		var targeted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var csproj in context.FindFiles(".csproj"))
		{
			var content = context.GetFileContent(csproj);
			foreach (var property in new[] { "TargetFramework", "TargetFrameworks" })
			{
				foreach (var value in MsBuildProperties.TryGetValues(content, property) ?? [])
				{
					targeted.UnionWith(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
				}
			}
		}

		return targeted;
	}
}
