using System.Xml.Linq;
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
		var packageReferences = GetPackageVersionReferences(context);
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

	private static List<PackageVersionReference> GetPackageVersionReferences(RepositoryContext context)
	{
		var references = new List<PackageVersionReference>();

		AddPackageVersionReferences(context.GetFileContent("Directory.Packages.props"), "Directory.Packages.props", references);

		foreach (var projectPath in context.FindFiles(".csproj"))
		{
			AddPackageReferenceVersions(context.GetFileContent(projectPath), projectPath, references);
		}

		return references;
	}

	private static void AddPackageVersionReferences(string? content, string filePath, List<PackageVersionReference> references)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return;
		}

		try
		{
			var doc = XDocument.Parse(content);
			foreach (var packageVersion in doc.Descendants("PackageVersion"))
			{
				var packageId = packageVersion.Attribute("Include")?.Value ?? packageVersion.Attribute("Update")?.Value;
				if (string.IsNullOrWhiteSpace(packageId))
				{
					continue;
				}

				var versionAttribute = packageVersion.Attribute("Version")?.Value;
				var versionElement = packageVersion.Element("Version")?.Value;
				var currentVersion = versionAttribute ?? versionElement;
				if (string.IsNullOrWhiteSpace(currentVersion))
				{
					continue;
				}

				references.Add(new PackageVersionReference(
					filePath,
					packageId,
					currentVersion,
					versionAttribute is not null ? "PackageVersionAttribute" : "PackageVersionElement"));
			}
		}
		catch
		{
		}
	}

	private static void AddPackageReferenceVersions(string? content, string filePath, List<PackageVersionReference> references)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return;
		}

		try
		{
			var doc = XDocument.Parse(content);
			foreach (var packageReference in doc.Descendants("PackageReference"))
			{
				var packageId = packageReference.Attribute("Include")?.Value ?? packageReference.Attribute("Update")?.Value;
				if (string.IsNullOrWhiteSpace(packageId))
				{
					continue;
				}

				var versionAttribute = packageReference.Attribute("Version")?.Value;
				var versionElement = packageReference.Element("Version")?.Value;
				var currentVersion = versionAttribute ?? versionElement;
				if (string.IsNullOrWhiteSpace(currentVersion))
				{
					continue;
				}

				references.Add(new PackageVersionReference(
					filePath,
					packageId,
					currentVersion,
					versionAttribute is not null ? "PackageReferenceAttribute" : "PackageReferenceElement"));
			}
		}
		catch
		{
		}
	}

	private sealed record PackageVersionReference(string FilePath, string PackageId, string CurrentVersion, string VersionKind);

	private sealed record PackageVersionFinding(string FilePath, string PackageId, string VersionKind, string CurrentVersion, string LatestVersion);
}
