namespace PanoramicData.NugetManagement.Services;

/// <summary>A commit as the regression guard sees it.</summary>
/// <param name="Hash">The commit hash.</param>
/// <param name="Subject">The first line of the message.</param>
/// <param name="RemediationTrailer">The value of the <see cref="GovernanceCommitMessage.TrailerKey"/> trailer, or null when absent.</param>
public sealed record GitCommit(string Hash, string Subject, string? RemediationTrailer = null);
