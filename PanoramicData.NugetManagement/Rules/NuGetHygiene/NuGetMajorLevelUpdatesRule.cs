using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks whether explicitly versioned NuGet packages are missing major-level updates.
/// </summary>
public sealed class NuGetMajorLevelUpdatesRule : NuGetPackageUpdateRuleBase
{
	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetMajorLevelUpdatesRule"/> class.
	/// </summary>
	public NuGetMajorLevelUpdatesRule()
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetMajorLevelUpdatesRule"/> class.
	/// </summary>
	/// <param name="versionStatusResolver">Resolves the latest version status for a package.</param>
	public NuGetMajorLevelUpdatesRule(Func<string, string, CancellationToken, Task<PackageVersionStatus?>> versionStatusResolver)
		: base(versionStatusResolver)
	{
	}

	/// <inheritdoc />
	public override string RuleId => "PKG-07";

	/// <inheritdoc />
	public override string RuleName => "Major-level NuGet packages up to date";

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Critical;

	/// <inheritdoc />
	protected override PackageUpdateLevel TargetUpdateLevel => PackageUpdateLevel.Major;

	/// <inheritdoc />
	protected override string UpdateLevelDisplayName => "major-level";
}
