using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks whether explicitly versioned NuGet packages are missing build-level updates.
/// </summary>
public sealed class NuGetBuildLevelUpdatesRule : NuGetPackageUpdateRuleBase
{
	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetBuildLevelUpdatesRule"/> class.
	/// </summary>
	public NuGetBuildLevelUpdatesRule()
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetBuildLevelUpdatesRule"/> class.
	/// </summary>
	/// <param name="versionStatusResolver">Resolves the latest version status for a package.</param>
	public NuGetBuildLevelUpdatesRule(Func<string, string, CancellationToken, Task<PackageVersionStatus?>> versionStatusResolver)
		: base(versionStatusResolver)
	{
	}

	/// <inheritdoc />
	public override string RuleId => "PKG-05";

	/// <inheritdoc />
	public override string RuleName => "Build-level NuGet packages up to date";

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	protected override PackageUpdateLevel TargetUpdateLevel => PackageUpdateLevel.Build;

	/// <inheritdoc />
	protected override string UpdateLevelDisplayName => "build-level";
}
