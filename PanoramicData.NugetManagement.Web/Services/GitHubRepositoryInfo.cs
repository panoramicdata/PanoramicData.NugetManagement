namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What discovery needs to know about one repository GitHub lists for an organisation.
/// </summary>
/// <param name="FullName">The repository as "owner/name".</param>
/// <param name="Url">The repository's web address, or null when GitHub gave none.</param>
/// <param name="IsArchived">Whether the repository is archived (read-only).</param>
/// <param name="IsFork">Whether the repository is a fork.</param>
public sealed record GitHubRepositoryInfo(string FullName, string? Url, bool IsArchived, bool IsFork);
