using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Base class for rules that enforce NuGet package freshness by semantic update level.
/// </summary>
public abstract class NuGetPackageUpdateRuleBase : RuleBase
{
	private readonly Func<string, string, CancellationToken, Task<PackageVersionStatus?>> _versionStatusResolver;

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetPackageUpdateRuleBase"/> class.
	/// </summary>
	protected NuGetPackageUpdateRuleBase()
		: this(new NuGetVersionChecker().GetVersionStatusAsync)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetPackageUpdateRuleBase"/> class.
	/// </summary>
	/// <param name="versionStatusResolver">Resolves the latest version status for a package.</param>
	protected NuGetPackageUpdateRuleBase(Func<string, string, CancellationToken, Task<PackageVersionStatus?>> versionStatusResolver)
	{
		_versionStatusResolver = versionStatusResolver;
	}

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <summary>
	/// Gets the update level this rule enforces.
	/// </summary>
	protected abstract PackageUpdateLevel TargetUpdateLevel { get; }

	/// <summary>
	/// Gets the user-facing label for the update level.
	/// </summary>
	protected abstract string UpdateLevelDisplayName { get; }

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var packageReferences = PackageReferenceScanner.Scan(context);
		if (packageReferences.Count == 0)
		{
			return Pass("No explicit NuGet package versions were found to evaluate.");
		}

		var matches = new List<PackageVersionFinding>();
		foreach (var packageReference in packageReferences)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var status = await _versionStatusResolver(packageReference.PackageId, packageReference.CurrentVersion, cancellationToken).ConfigureAwait(false);
			if (status is null || status.UpdateLevel != TargetUpdateLevel)
			{
				continue;
			}

			matches.Add(new PackageVersionFinding(
				packageReference.FilePath,
				packageReference.PackageId,
				packageReference.VersionKind,
				status.CurrentVersion,
				status.LatestVersion));
		}

		if (matches.Count == 0)
		{
			return Pass($"No {UpdateLevelDisplayName} NuGet package updates are available.");
		}

		var orderedMatches = matches
			.OrderBy(match => match.PackageId, StringComparer.OrdinalIgnoreCase)
			.ThenBy(match => match.FilePath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		return Fail(
			$"The following NuGet packages have {UpdateLevelDisplayName} updates available: {string.Join("; ", orderedMatches.Select(FormatFinding))}",
			new RuleAdvisory
			{
				Summary = $"Update packages with {UpdateLevelDisplayName} updates to their latest stable versions.",
				Detail = $"One or more explicit NuGet package versions are behind the latest stable version on nuget.org by a {UpdateLevelDisplayName} update. Update the listed package versions in `Directory.Packages.props` or the affected project files.",
				Data = new()
				{
					["remediation_type"] = "update_package_versions",
					["updates"] = orderedMatches.Select(SerializeFinding).ToArray()
				}
			});
	}

	private static string FormatFinding(PackageVersionFinding finding)
		=> $"{finding.PackageId} {finding.CurrentVersion} → {finding.LatestVersion} ({finding.FilePath})";

	private static string SerializeFinding(PackageVersionFinding finding)
		=> string.Join('|', finding.FilePath, finding.PackageId, finding.VersionKind, finding.CurrentVersion, finding.LatestVersion);

	private sealed record PackageVersionFinding(string FilePath, string PackageId, string VersionKind, string CurrentVersion, string LatestVersion);
}
