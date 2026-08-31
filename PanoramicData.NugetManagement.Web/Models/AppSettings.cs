using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Application settings bound from configuration/user secrets.
/// </summary>
public class AppSettings
{
	/// <summary>
	/// The GitHub organization name to manage.
	/// </summary>
	public string GitHubOrganization { get; set; } = string.Empty;

	/// <summary>
	/// The NuGet organization/owner name for package discovery.
	/// </summary>
	public string NuGetOrganization { get; set; } = string.Empty;

	/// <summary>
	/// GitHub OAuth App Client ID.
	/// </summary>
	public string GitHubClientId { get; set; } = string.Empty;

	/// <summary>
	/// GitHub OAuth App Client Secret.
	/// </summary>
	public string GitHubClientSecret { get; set; } = string.Empty;

	/// <summary>
	/// The NuGet user for Trusted Publishing login.
	/// </summary>
	public string NuGetUser { get; set; } = Standards.NuGetUser;

	/// <summary>
	/// The expected SPDX license expression (e.g. "MIT").
	/// </summary>
	public string ExpectedLicense { get; set; } = "MIT";

	/// <summary>
	/// The expected copyright holder name.
	/// </summary>
	public string CopyrightHolder { get; set; } = "Panoramic Data Limited";

	/// <summary>
	/// Optional Codacy API token for code quality checks.
	/// </summary>
	public string? CodacyApiToken { get; set; }

	/// <summary>
	/// The root directory the app clones repositories into, as <c>&lt;root&gt;/&lt;owner&gt;/&lt;name&gt;</c>.
	/// Defaults to a <c>.nugetmanagement-repos</c> directory beside this application's own repository.
	/// </summary>
	/// <remarks>
	/// Point this somewhere of the app's own, never at a directory holding working copies you edit
	/// yourself: the app commits with <c>git add -A</c>, so anything uncommitted it finds in a repository
	/// it is fixing would be committed and pushed along with the fix.
	/// </remarks>
	public string? LocalReposRoot { get; set; }

	/// <summary>
	/// Development-only: when true (and the environment is Development), the GitHub OAuth
	/// sign-in is bypassed with a synthetic local identity. Never honoured in Production.
	/// </summary>
	public bool DevAuthBypass { get; set; }

	/// <summary>
	/// The user name presented by the development auth bypass.
	/// </summary>
	public string DevAuthUser { get; set; } = "dev";

	/// <summary>
	/// Optional GitHub Personal Access Token (classic) surfaced as the access_token under the
	/// development auth bypass, enabling GitHub API calls (e.g. assessing un-cloned repositories)
	/// without the interactive OAuth sign-in. Requires the same scopes the OAuth flow requests:
	/// <c>repo</c> (read/write repository contents) and <c>read:org</c> (read organization membership).
	/// Generate one at https://github.com/settings/tokens (Tokens (classic)).
	/// </summary>
	public string? GitHubPat { get; set; }

	/// <summary>
	/// The repository that gets an issue when Dependabot raises a valid pull request no remediation
	/// covers — that is, this application's own repository.
	/// </summary>
	/// <remarks>
	/// Configurable rather than hardcoded, because a fork or a differently-named deployment should
	/// file its missing-remediation work against itself, not against ours.
	/// </remarks>
	public string GovernanceIssueRepository { get; set; } = "panoramicdata/PanoramicData.NugetManagement";
}
