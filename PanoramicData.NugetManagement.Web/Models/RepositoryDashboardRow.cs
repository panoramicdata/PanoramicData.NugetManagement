using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Assessment result enriched with local filesystem state for a single repository.
/// </summary>
/// <remarks>
/// Keyed on the repository, because that is what every action this application takes acts on:
/// cloning, assessing, branching, committing, building and publishing are all repository-shaped.
/// While the row was keyed on a package, a repository publishing four packages was four rows, and
/// was cloned, assessed and remediated four times for one set of findings.
/// </remarks>
public class RepositoryDashboardRow
{
	/// <summary>
	/// The GitHub repository full name (owner/repo). The identity of the row.
	/// </summary>
	public required string RepositoryFullName { get; set; }

	/// <summary>
	/// The packages this repository publishes. A repository can publish many —
	/// PanoramicData.ECharts publishes four — and they version independently, so the version and the
	/// tag comparison belong here rather than on the repository.
	/// </summary>
	public List<PublishedPackage> Packages { get; set; } = [];

	/// <summary>
	/// Whether any published package is at a version other than the repository's latest tag.
	/// </summary>
	public bool AnyPackageOutOfStepWithTag
		=> Packages.Any(package => package.MatchesTag(LatestTag) == false);

	/// <summary>
	/// The organisation this repository's packages were discovered under. Used to group rows into the
	/// per-organisation branches of the navigation tree and to scope re-assessment.
	/// Not the same as the owner segment of <see cref="RepositoryFullName"/>, which can differ
	/// for vendored or forked packages.
	/// </summary>
	public string Organization { get; set; } = string.Empty;

	/// <summary>
	/// Whether this repository is ours to govern.
	/// </summary>
	/// <remarks>
	/// A package we publish can name a repository we do not own — a repackage declares the upstream it
	/// was built from — and owning the package is no licence to clone, assess or write to that.
	/// </remarks>
	public bool IsGoverned { get; set; } = true;

	/// <summary>
	/// Why this repository is not governed, or null when it is.
	/// </summary>
	public string? NotGovernedReason { get; set; }

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
	/// The full path to the .slnx file in the local repository, if found.
	/// </summary>
	public string? SlnxPath { get; set; }

	/// <summary>
	/// Whether the local working tree is clean.
	/// </summary>
	public bool? IsWorkingTreeClean { get; set; }

	/// <summary>
	/// The current local branch name.
	/// </summary>
	public string? CurrentBranch { get; set; }

	/// <summary>
	/// Whether the local branch is in sync with its origin counterpart
	/// (i.e. not behind and not ahead after a fetch).
	/// </summary>
	public bool? IsSyncedWithOrigin { get; set; }

	/// <summary>
	/// When <see cref="IsSyncedWithOrigin"/> was last established, or null if it never has been.
	/// </summary>
	/// <remarks>
	/// Establishing it costs a fetch, so it is a point-in-time answer that nothing keeps current: origin
	/// can move the moment after. The age is what lets "we are in sync" disable the Sync button without
	/// stranding anyone — past a short window the claim is treated as no longer worth trusting, and the
	/// button becomes available again.
	/// </remarks>
	public DateTimeOffset? SyncStatusCheckedAtUtc { get; set; }

	/// <summary>
	/// The latest git tag on the local repo (e.g. "1.0.55").
	/// </summary>
	public string? LatestTag { get; set; }

	/// <summary>
	/// The assessment result from the governance rules. Null if not yet assessed.
	/// </summary>
	public RepoAssessment? Assessment { get; set; }

	/// <summary>
	/// Whether this row is currently being reassessed.
	/// Used to show a spinner in the tree while awaiting reassessment.
	/// </summary>
	public bool IsReassessing { get; set; }

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
	/// Total number of critical findings.
	/// </summary>
	public int TotalCriticals => Assessment?.CriticalCount ?? 0;

	/// <summary>
	/// Total number of errors.
	/// </summary>
	public int TotalErrors => Assessment?.ErrorCount ?? 0;

	/// <summary>
	/// Total number of warnings.
	/// </summary>
	public int TotalWarnings => Assessment?.WarningCount ?? 0;

	/// <summary>
	/// Shared health state used by both tree and toolbar visuals.
	/// </summary>
	public PackageHealthStatus HealthStatus
	{
		get
		{
			// Spinner only while an assessment is genuinely in progress.
			if (IsReassessing || Status is PackageStatus.Assessing)
			{
				return PackageHealthStatus.Pending;
			}

			// No assessment and nothing running → not assessable / not yet assessed (static icon, no spinner).
			if (Assessment is null)
			{
				return PackageHealthStatus.Unknown;
			}

			if (TotalFailures == 0)
			{
				return PackageHealthStatus.Success;
			}

			if (TotalCriticals > 0 || TotalErrors > 0)
			{
				return PackageHealthStatus.Error;
			}

			return TotalWarnings > 0
				? PackageHealthStatus.Warning
				: PackageHealthStatus.Info;
		}
	}
}

/// <summary>
/// One NuGet package published by a repository.
/// </summary>
public class PublishedPackage
{
	/// <summary>The NuGet package identifier.</summary>
	public required string PackageId { get; init; }

	/// <summary>The latest published version on NuGet.</summary>
	public string? LatestVersion { get; set; }

	/// <summary>
	/// Whether this package's published version matches the repository's latest tag, or null when
	/// either is unknown.
	/// </summary>
	/// <param name="latestTag">The repository's latest git tag.</param>
	public bool? MatchesTag(string? latestTag)
		=> LatestVersion is not null && latestTag is not null
			? string.Equals(LatestVersion, latestTag, StringComparison.OrdinalIgnoreCase)
			: null;
}
