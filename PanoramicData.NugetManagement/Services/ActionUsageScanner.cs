using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// One <c>uses:</c> step, reduced to the action it names and the major version it pins.
/// </summary>
/// <param name="Action">
/// The action's repository, as <c>owner/name</c> — never including a sub-action path. A usage of
/// <c>github/codeql-action/init</c> is a usage of <c>github/codeql-action</c>, because that is the
/// repository Dependabot versions and bumps; its sub-actions have no versions of their own.
/// </param>
/// <param name="SubPath">
/// The sub-action within the repository, such as <c>init</c>, or null for a plain action. Kept
/// because anything rewriting the <c>uses:</c> line needs the whole of it back.
/// </param>
/// <param name="VersionSpec">The spec exactly as written, e.g. <c>v3.1.2</c> or a commit SHA.</param>
/// <param name="MajorVersion">
/// The major version the spec pins, or null when the spec is not a version at all — a commit SHA,
/// a branch name, or anything else unreadable.
/// </param>
/// <param name="WorkflowPath">The workflow the step appears in.</param>
public sealed record ActionUsage(
	string Action,
	string? SubPath,
	string VersionSpec,
	int? MajorVersion,
	string WorkflowPath);

/// <summary>
/// Reads the versions of the GitHub Actions a repository's workflows actually use.
/// </summary>
/// <remarks>
/// The CI rules each parse <c>uses:</c> steps inline for their own action. This reads every action in
/// every workflow, because triage must answer the question for whichever action a Dependabot pull
/// request happens to name. Consolidating the rules onto this is a separate cleanup.
/// </remarks>
public static partial class ActionUsageScanner
{
	private const string _workflowsPrefix = ".github/workflows/";

	/// <summary>
	/// Every versioned action usage across the repository's workflows.
	/// </summary>
	/// <param name="context">The repository to read.</param>
	public static List<ActionUsage> Scan(RepositoryContext context)
	{
		var usages = new List<ActionUsage>();

		foreach (var (path, content) in context.FileContents)
		{
			if (!path.Replace('\\', '/').StartsWith(_workflowsPrefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			foreach (var match in UsesStep().Matches(content).Cast<Match>())
			{
				var spec = match.Groups["spec"].Value;
				var (action, subPath) = SplitRepository(match.Groups["action"].Value);

				usages.Add(new ActionUsage(action, subPath, spec, MajorOf(spec), path));
			}
		}

		return usages;
	}

	/// <summary>
	/// The weakest major version at which an action is used, or null when any usage is unreadable or
	/// the action is not used at all.
	/// </summary>
	/// <param name="usages">The scanned usages.</param>
	/// <param name="action">The action to measure, as <c>owner/name</c>.</param>
	/// <remarks>
	/// The lowest usage decides, because one workflow left behind means a version bump still has work
	/// to do. An unreadable usage poisons the answer rather than being skipped: treating a SHA-pinned
	/// step as absent would let triage call a pull request satisfied on the strength of the other
	/// workflows, and close it while a real usage sat at an old version.
	/// </remarks>
	public static int? LowestMajorOf(IReadOnlyList<ActionUsage> usages, string action)
	{
		var matching = usages
			.Where(u => string.Equals(u.Action, action, StringComparison.OrdinalIgnoreCase))
			.ToList();

		return matching.Count == 0 || matching.Any(u => u.MajorVersion is null)
			? null
			: matching.Min(u => u.MajorVersion);
	}

	/// <summary>
	/// Splits <c>owner/name/sub/path</c> into the repository and everything after it.
	/// </summary>
	/// <remarks>
	/// A sub-action has no version of its own: <c>github/codeql-action/init@v4</c> is version 4 of the
	/// <c>github/codeql-action</c> repository. Attributing the usage to the full path instead would mean
	/// a repository already on v4 could never be shown to satisfy a bump to v4, because the name
	/// Dependabot uses and the name the workflow writes would never match.
	/// </remarks>
	private static (string Repository, string? SubPath) SplitRepository(string uses)
	{
		var segments = uses.Split('/');

		return segments.Length <= 2
			? (uses, null)
			: ($"{segments[0]}/{segments[1]}", string.Join('/', segments[2..]));
	}

	/// <summary>
	/// The major version a spec pins, or null when the spec is not a version.
	/// </summary>
	private static int? MajorOf(string spec)
	{
		var match = MajorVersion().Match(spec);

		return match.Success && int.TryParse(match.Groups["major"].Value, out var major)
			? major
			: null;
	}

	/// <summary>
	/// A <c>uses:</c> step naming an <c>owner/name@spec</c> action. Local (<c>./...</c>) and container
	/// (<c>docker://...</c>) steps do not match, because neither is a versioned action Dependabot
	/// would ever raise a pull request for.
	/// </summary>
	[GeneratedRegex(
		@"^\s*-?\s*uses:\s*(?<action>[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+?)@(?<spec>[^\s#]+)",
		RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex UsesStep();

	[GeneratedRegex(@"^v?(?<major>\d+)(?:\.\d+)*$", RegexOptions.CultureInvariant)]
	private static partial Regex MajorVersion();
}
