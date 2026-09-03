namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// One dependency a Dependabot pull request would move.
/// </summary>
/// <param name="Dependency">The dependency.</param>
/// <param name="FromVersion">The version Dependabot believes is declared.</param>
/// <param name="ToVersion">The version it would move to.</param>
/// <param name="Directory">The sub-directory it applies to, or null when none was named.</param>
public sealed record DependabotBump(
	DependencyRef Dependency,
	string FromVersion,
	string ToVersion,
	string? Directory);

/// <summary>
/// What one Dependabot pull request proposes.
/// </summary>
/// <param name="Number">The pull request number.</param>
/// <param name="Bumps">
/// Every dependency it would move, in the order the pull request lists them. Never empty: a pull
/// request proposing nothing readable parses to no proposal at all rather than an empty one, so that
/// "we read it and it said nothing" can never be mistaken for "it proposes nothing".
/// </param>
/// <param name="HtmlUrl">The pull request's web address.</param>
/// <remarks>
/// A list rather than a single dependency because Dependabot groups updates: a grouped pull request
/// moves several dependencies at once, and its title names neither all of them nor any version. A
/// single-dependency pull request is a proposal with one bump, so nothing downstream special-cases
/// either shape.
/// </remarks>
public sealed record DependabotProposal(
	int Number,
	IReadOnlyList<DependabotBump> Bumps,
	string HtmlUrl);
