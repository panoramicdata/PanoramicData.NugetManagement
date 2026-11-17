namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// What a caller already knows about a local clone, handed to <see cref="LocalRepositoryContextBuilder"/>
/// because the files on disk cannot say it.
/// </summary>
public sealed class LocalContextFacts
{
	/// <summary>Gets the default branch name (used for context only).</summary>
	public string DefaultBranch { get; init; } = "main";

	/// <summary>Gets the currently checked out local branch when known.</summary>
	public string? CurrentBranch { get; init; }

	/// <summary>Gets the newest version tag, when known, for CI-11.</summary>
	public string? LatestTag { get; init; }

	/// <summary>Gets the newest version on nuget.org, when known, for CI-11.</summary>
	public string? LatestPublishedVersion { get; init; }

	/// <summary>
	/// Gets the CI run for <see cref="LatestTag"/>, when known. Lets CI-11 stay quiet about a release
	/// that is still in flight and gives CI-13 its subject.
	/// </summary>
	public Models.ReleaseRun? ReleaseRun { get; init; }

	/// <summary>
	/// Gets the commit HEAD points at, when known. Lets CQ-06 tell grades measured on the checked-out
	/// commit from grades Codacy measured on an older one.
	/// </summary>
	public string? HeadSha { get; init; }

	/// <summary>Gets the version a release from this clone would carry, when nbgv could be asked, for VER-05.</summary>
	public string? NextVersion { get; init; }

	/// <summary>
	/// Gets whether this clone was recently confirmed to match origin. VER-05 reports nothing without it,
	/// because a stale clone's own commits look exactly like work waiting to be released.
	/// </summary>
	public bool IsConfirmedInSyncWithOrigin { get; init; }

	/// <summary>
	/// Gets the line coverage Codacy holds for the repository, resolved by the caller because the clone
	/// cannot know it. Null leaves TST-10 reporting RED, so the local path must supply it.
	/// </summary>
	public double? LineCoveragePercent { get; init; }

	/// <summary>Gets whether GitHub private vulnerability reporting is on, resolved by the caller; null when unknown.</summary>
	public bool? PrivateVulnerabilityReportingEnabled { get; init; }

	/// <summary>Gets whether the repository is private, or null when GitHub could not be asked.</summary>
	public bool? IsPrivateRepository { get; init; }
}
