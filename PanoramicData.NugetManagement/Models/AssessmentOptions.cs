namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Options for configuring the organization-level assessment.
/// </summary>
public class AssessmentOptions
{
	/// <summary>
	/// The GitHub organization name to assess.
	/// </summary>
	public required string OrganizationName { get; init; }

	/// <summary>
	/// Per-repository option overrides, keyed by repository name (not full name).
	/// </summary>
	public Dictionary<string, RepoOptions> RepositoryOptions { get; init; } = [];

	/// <summary>
	/// Whether to query the NuGet API to check for up-to-date package versions.
	/// This can be slow for organizations with many repositories.
	/// </summary>
	public bool CheckNuGetVersions { get; init; } = true;

	/// <summary>
	/// Whether to only assess repositories that produce NuGet packages.
	/// When false, all non-excluded repositories are assessed.
	/// </summary>
	public bool OnlyPackableRepositories { get; init; }

	/// <summary>
	/// Organization-level Codacy API token. When set, it is used for every repository that does not
	/// supply its own <see cref="CodacyOptions.ApiToken"/>, enabling Codacy issue ingestion across
	/// the organization from a single credential.
	/// </summary>
	public string? CodacyApiToken { get; init; }

	/// <summary>
	/// Whether to query Codacy for each repository's open issues. Has effect only when a Codacy API
	/// token is available (organization-level or per-repository). Can be slow for large organizations.
	/// </summary>
	public bool CheckCodacyIssues { get; init; } = true;

	/// <summary>
	/// Maximum number of repositories to assess concurrently.
	/// </summary>
	public int MaxConcurrency { get; init; } = 5;
}
