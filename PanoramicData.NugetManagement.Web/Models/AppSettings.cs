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
	/// The local root directory where sibling repos are cloned.
	/// Defaults to the parent of the current working directory.
	/// </summary>
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
}
