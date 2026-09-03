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
	/// Initializes a new instance with explicit stores and clock, for tests.
	/// </summary>
	/// <param name="cache">The committed upstream snapshot.</param>
	/// <param name="floors">The estate-learned floors.</param>
	/// <param name="timeProvider">The clock the grace period is measured against.</param>
	public NuGetBuildLevelUpdatesRule(
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		TimeProvider timeProvider)
		: base(cache, floors, timeProvider)
	{
	}

	/// <summary>
	/// Initializes a new instance with explicit stores, clock and owned-package list, for tests.
	/// </summary>
	/// <param name="cache">The committed upstream snapshot.</param>
	/// <param name="floors">The estate-learned floors.</param>
	/// <param name="timeProvider">The clock the grace period is measured against.</param>
	/// <param name="owned">The packages the estate publishes itself.</param>
	public NuGetBuildLevelUpdatesRule(
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		TimeProvider timeProvider,
		NuGetOwnedPackageCatalog owned)
		: base(cache, floors, timeProvider, owned)
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

	/// <inheritdoc />
	protected override int GraceDays => 30;
}
