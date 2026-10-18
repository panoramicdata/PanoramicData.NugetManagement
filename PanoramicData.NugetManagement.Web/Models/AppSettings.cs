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
	/// A bearer token that switches on the loopback-only <c>/agent</c> API, which lets a coding agent read
	/// the tree's state and queue a few kinds of work. Empty, the default, means the API does not exist.
	/// </summary>
	/// <remarks>
	/// Meant to be set per launch, from the environment (<c>AppSettings__AgentApiToken</c>), and never
	/// committed: anyone holding it on this machine can queue work against the local clones. When this is
	/// empty the application falls back to <c>agent-token.txt</c> in its local application data folder, if
	/// that file exists, so a restart by another session does not lock the agent out.
	/// </remarks>
	public string? AgentApiToken { get; set; }

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
	/// The id of a GitHub App for the work runner to act as. With a private key and an installation, the
	/// background work (organisation refreshes, assessments, issue filing) uses the App's installation
	/// token, which has a rate limit of its own, instead of the signed-in user's token.
	/// </summary>
	/// <remarks>
	/// GitHub applies its 5,000-an-hour REST limit per user, so one large refresh on the user's token can
	/// stop everything else that user runs. Leave empty to keep using the signed-in user's token.
	/// Once set, a problem with the App is an error: the runner never falls back to the user's token.
	/// </remarks>
	public string? GitHubAppId { get; set; }

	/// <summary>
	/// The installation of the App on the organisation. Optional: when empty, the App's single installation
	/// on <see cref="GitHubOrganization"/> is found automatically.
	/// </summary>
	public long? GitHubAppInstallationId { get; set; }

	/// <summary>
	/// The App's private key, PEM-encoded. Prefer <see cref="GitHubAppPrivateKeyFile"/>, or an environment
	/// variable (<c>AppSettings__GitHubAppPrivateKey</c>, where <c>\n</c> may stand for a newline), and never
	/// commit it.
	/// </summary>
	public string? GitHubAppPrivateKey { get; set; }

	/// <summary>
	/// Path to a file holding the App's PEM-encoded private key, used when
	/// <see cref="GitHubAppPrivateKey"/> is empty.
	/// </summary>
	public string? GitHubAppPrivateKeyFile { get; set; }

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
