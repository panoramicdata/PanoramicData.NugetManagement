using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Reads what a Dependabot pull request proposes, from its title.
/// </summary>
/// <remarks>
/// Deliberately fails silent rather than open: anything it does not recognise returns null, and
/// triage then leaves that pull request strictly alone. Guessing would mean closing pull requests
/// nobody understood. The cost is that grouped pull requests — "Bump the nuget group with 3 updates"
/// — are invisible to triage and only a human clears them.
/// </remarks>
public static partial class DependabotTitleParser
{
	/// <summary>The only author whose pull requests are eligible for triage.</summary>
	public const string DependabotLogin = "dependabot[bot]";

	/// <summary>
	/// The proposal a pull request describes, or null when it is not a single-dependency version bump
	/// raised by Dependabot.
	/// </summary>
	/// <param name="issue">The open item, as the issue list reports it.</param>
	public static DependabotProposal? Parse(RepositoryIssue issue)
	{
		if (!issue.IsPullRequest
			|| !string.Equals(issue.AuthorLogin, DependabotLogin, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var match = BumpTitle().Match(issue.Title);

		if (!match.Success)
		{
			return null;
		}

		var name = match.Groups["name"].Value;
		var directory = match.Groups["dir"];

		return new DependabotProposal(
			issue.Number,
			new DependencyRef(EcosystemOf(name), name),
			match.Groups["from"].Value,
			match.Groups["to"].Value,
			directory.Success ? directory.Value : null,
			issue.HtmlUrl);
	}

	/// <summary>
	/// A name containing a slash is an <c>owner/name</c> action; anything else is a NuGet package.
	/// </summary>
	/// <remarks>
	/// Inferred rather than declared because the pull request title is all there is to go on, and the
	/// two ecosystems this application governs happen to be unambiguous on that one character.
	/// </remarks>
	private static DependencyEcosystem EcosystemOf(string name)
		=> name.Contains('/', StringComparison.Ordinal)
			? DependencyEcosystem.GitHubActions
			: DependencyEcosystem.NuGet;

	[GeneratedRegex(
		@"^Bump (?<name>\S+) from (?<from>\S+) to (?<to>\S+)(?: in (?<dir>\S+))?$",
		RegexOptions.CultureInvariant)]
	private static partial Regex BumpTitle();
}
