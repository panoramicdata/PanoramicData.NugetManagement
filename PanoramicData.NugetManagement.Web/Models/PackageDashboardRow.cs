using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Assessment result enriched with local filesystem state for a single repository.
/// </summary>
public class PackageDashboardRow
{
    /// <summary>
    /// The NuGet package ID.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    /// The latest published version on NuGet.
    /// </summary>
    public string? LatestVersion { get; set; }

    /// <summary>
    /// The GitHub repository full name (owner/repo).
    /// </summary>
    public string? RepositoryFullName { get; set; }

    /// <summary>
    /// The GitHub repository URL.
    /// </summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>
    /// Whether the repository is cloned locally.
    /// </summary>
    public bool IsClonedLocally { get; set; }

    /// <summary>
    /// The local filesystem path to the cloned repository.
    /// </summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// Whether the local working tree is clean.
    /// </summary>
    public bool? IsWorkingTreeClean { get; set; }

    /// <summary>
    /// The current local branch name.
    /// </summary>
    public string? CurrentBranch { get; set; }

    /// <summary>
    /// The assessment result from the governance rules. Null if not yet assessed.
    /// </summary>
    public RepoAssessment? Assessment { get; set; }

    /// <summary>
    /// Issue counts grouped by category.
    /// </summary>
    public Dictionary<AssessmentCategory, CategorySummary> CategorySummaries { get; set; } = [];

    /// <summary>
    /// Current remediation/operation status.
    /// </summary>
    public PackageStatus Status { get; set; } = PackageStatus.NotAssessed;

    /// <summary>
    /// Status message for the current operation.
    /// </summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// Total number of failed rules.
    /// </summary>
    public int TotalFailures => Assessment?.FailedCount ?? 0;

    /// <summary>
    /// Total number of errors.
    /// </summary>
    public int TotalErrors => Assessment?.ErrorCount ?? 0;

    /// <summary>
    /// Total number of warnings.
    /// </summary>
    public int TotalWarnings => Assessment?.WarningCount ?? 0;
}

/// <summary>
/// Summary of rule results for a single category.
/// </summary>
public class CategorySummary
{
    /// <summary>
    /// Number of rules that passed.
    /// </summary>
    public int Passed { get; set; }

    /// <summary>
    /// Number of error-severity failures.
    /// </summary>
    public int Errors { get; set; }

    /// <summary>
    /// Number of warning-severity failures.
    /// </summary>
    public int Warnings { get; set; }

    /// <summary>
    /// Number of info-severity failures.
    /// </summary>
    public int Infos { get; set; }

    /// <summary>
    /// Total failures across all severities.
    /// </summary>
    public int TotalFailures => Errors + Warnings + Infos;
}

/// <summary>
/// Status of a package row in the dashboard.
/// </summary>
public enum PackageStatus
{
    /// <summary>
    /// Not yet assessed.
    /// </summary>
    NotAssessed,

    /// <summary>
    /// Assessment is in progress.
    /// </summary>
    Assessing,

    /// <summary>
    /// Assessment complete — review results.
    /// </summary>
    Assessed,

    /// <summary>
    /// Remediation is in progress.
    /// </summary>
    Remediating,

    /// <summary>
    /// Remediation complete — ready for testing.
    /// </summary>
    Remediated,

    /// <summary>
    /// Tests are running.
    /// </summary>
    Testing,

    /// <summary>
    /// Tests passed — ready to publish.
    /// </summary>
    TestsPassed,

    /// <summary>
    /// Tests failed.
    /// </summary>
    TestsFailed,

    /// <summary>
    /// Publishing in progress.
    /// </summary>
    Publishing,

    /// <summary>
    /// Published successfully.
    /// </summary>
    Published,

    /// <summary>
    /// An error occurred.
    /// </summary>
    Error,

    /// <summary>
    /// Not cloned locally.
    /// </summary>
    NotCloned,

    /// <summary>
    /// Cloning in progress.
    /// </summary>
    Cloning
}
