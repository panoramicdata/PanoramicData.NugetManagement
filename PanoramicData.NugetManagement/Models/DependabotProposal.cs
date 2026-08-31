namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// What one Dependabot pull request proposes, read from its title.
/// </summary>
/// <param name="Number">The pull request number.</param>
/// <param name="Dependency">The dependency it would move.</param>
/// <param name="FromVersion">The version it believes is declared.</param>
/// <param name="ToVersion">The version it would move to.</param>
/// <param name="Directory">The sub-directory it applies to, or null when the title names none.</param>
/// <param name="HtmlUrl">The pull request's web address.</param>
public sealed record DependabotProposal(
	int Number,
	DependencyRef Dependency,
	string FromVersion,
	string ToVersion,
	string? Directory,
	string HtmlUrl);
