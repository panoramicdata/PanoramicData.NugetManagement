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
}
