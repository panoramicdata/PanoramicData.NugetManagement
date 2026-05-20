namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Represents a set of repositories that share a common pending NuGet package update.
/// </summary>
public class NuGetUpdateGroup
{
	/// <summary>
	/// The NuGet package ID being updated.
	/// </summary>
	public required string PackageId { get; init; }

	/// <summary>
	/// The currently installed version across all affected repositories.
	/// </summary>
	public required string CurrentVersion { get; init; }

	/// <summary>
	/// The latest available version on NuGet.org.
	/// </summary>
	public required string LatestVersion { get; init; }

	/// <summary>
	/// The full names (owner/repo) of the repositories that have this update available.
	/// </summary>
	public required List<string> AffectedRepoNames { get; init; }

	/// <summary>
	/// Number of affected repositories.
	/// </summary>
	public int AffectedCount => AffectedRepoNames.Count;
}
