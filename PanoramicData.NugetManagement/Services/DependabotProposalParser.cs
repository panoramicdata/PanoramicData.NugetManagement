using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Reads what a Dependabot pull request proposes, from its body where there is one and its title
/// otherwise.
/// </summary>
/// <remarks>
/// The body is the authority because a grouped pull request's title is not readable: "Bump
/// Microsoft.Extensions.DependencyInjection and 2 others" names neither the other two dependencies
/// nor a single version, so no title pattern can recover them. Dependabot writes the full list into
/// the body, one line per dependency, and that is what this reads.
/// <para>
/// The title remains a fallback, for a row restored from the cache: bodies are not persisted, and a
/// single-dependency title is perfectly readable on its own. A grouped title with no body yields
/// nothing, which is exactly the behaviour that existed before bodies were read.
/// </para>
/// <para>
/// Still fails silent rather than open: anything unrecognised returns null and triage leaves that
/// pull request strictly alone. Guessing would mean closing pull requests nobody understood.
/// </para>
/// </remarks>
public static partial class DependabotProposalParser
{
	/// <summary>The only author whose pull requests are eligible for triage.</summary>
	public const string DependabotLogin = "dependabot[bot]";

	/// <summary>
	/// What a pull request proposes, or null when it is not a readable Dependabot version bump.
	/// </summary>
	/// <param name="issue">The open item, as the issue list reports it.</param>
	public static DependabotProposal? Parse(RepositoryIssue issue)
	{
		if (!issue.IsPullRequest
			|| !string.Equals(issue.AuthorLogin, DependabotLogin, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var bumps = BumpsFromBody(issue.Body);

		if (bumps.Count == 0)
		{
			bumps = BumpsFromTitle(issue.Title);
		}

		return bumps.Count == 0
			? null
			: new DependabotProposal(issue.Number, bumps, issue.HtmlUrl);
	}

	/// <summary>
	/// Every dependency move the body states, deduplicated.
	/// </summary>
	/// <remarks>
	/// Dependabot lists a dependency once per manifest it appears in, so the same move can be stated
	/// twice. Left in, it would be written twice and counted twice.
	/// </remarks>
	/// <param name="body">The pull request body, or null when none was fetched.</param>
	private static List<DependabotBump> BumpsFromBody(string? body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return [];
		}

		var seen = new HashSet<(string Name, string To)>();
		var bumps = new List<DependabotBump>();

		foreach (var match in BodyLine().Matches(body).Cast<Match>())
		{
			var name = match.Groups["name"].Value;
			var to = match.Groups["to"].Value;

			if (!seen.Add((name.ToLowerInvariant(), to)))
			{
				continue;
			}

			var directory = match.Groups["dir"];

			bumps.Add(new DependabotBump(
				new DependencyRef(EcosystemOf(name), name),
				match.Groups["from"].Value,
				to,
				directory.Success ? directory.Value : null));
		}

		return bumps;
	}

	/// <summary>
	/// The single move a title states, or none when the title is not a single-dependency bump.
	/// </summary>
	/// <param name="title">The pull request title.</param>
	private static List<DependabotBump> BumpsFromTitle(string title)
	{
		var match = BumpTitle().Match(title);

		if (!match.Success)
		{
			return [];
		}

		var name = match.Groups["name"].Value;
		var directory = match.Groups["dir"];

		return
		[
			new DependabotBump(
				new DependencyRef(EcosystemOf(name), name),
				match.Groups["from"].Value,
				match.Groups["to"].Value,
				directory.Success ? directory.Value : null)
		];
	}

	/// <summary>
	/// A name containing a slash is an <c>owner/name</c> action; anything else is a NuGet package.
	/// </summary>
	/// <remarks>
	/// Inferred rather than declared because the pull request is all there is to go on, and the two
	/// ecosystems this application governs happen to be unambiguous on that one character.
	/// </remarks>
	private static DependencyEcosystem EcosystemOf(string name)
		=> name.Contains('/', StringComparison.Ordinal)
			? DependencyEcosystem.GitHubActions
			: DependencyEcosystem.NuGet;

	/// <summary>
	/// One dependency move as a body states it. Dependabot writes
	/// <c>Updated [X](url) from a to b.</c>, the same form for a single-dependency pull request as for
	/// each line of a grouped one.
	/// </summary>
	/// <remarks>
	/// The verb and name alternations are wider than the bodies this was built against need —
	/// <c>Updates `X`</c> and <c>Bumps [X](url)</c> are forms Dependabot has used elsewhere. Being
	/// permissive costs nothing and means a wording change upstream degrades to reading fewer pull
	/// requests rather than none.
	/// <para>
	/// Anchored to the start of a line, so the sentences inside the release-notes and changelog
	/// <c>&lt;details&gt;</c> blocks — which quote upstream text about other packages entirely — are
	/// not read as proposals.
	/// </para>
	/// <para>
	/// The version and directory groups are both lazy so that the full stop Dependabot ends the line
	/// with stays out of them, while a version's own dots and a path's own dots still land inside.
	/// Greedy groups here silently produce a version of <c>10.0.9.</c> or a directory of
	/// <c>/Athonet.Api.</c>, neither of which matches anything the repository declares — a rewrite that
	/// finds nothing rather than an error.
	/// </para>
	/// </remarks>
	[GeneratedRegex(
		@"^(?:Updated|Updates|Bumped|Bumps)\s+"
			+ @"(?:`(?<name>[^`]+)`|\[(?<name>[^\]]+)\]\([^)]*\)|(?<name>[^\s\[`]+))"
			+ @"\s+from\s+(?<from>\S+?)\s+to\s+(?<to>.+?)(?:\s+in\s+(?<dir>\S+?))?\.?\s*$",
		RegexOptions.CultureInvariant | RegexOptions.Multiline)]
	private static partial Regex BodyLine();

	[GeneratedRegex(
		@"^Bump (?<name>\S+) from (?<from>\S+) to (?<to>\S+)(?: in (?<dir>\S+))?$",
		RegexOptions.CultureInvariant)]
	private static partial Regex BumpTitle();
}
