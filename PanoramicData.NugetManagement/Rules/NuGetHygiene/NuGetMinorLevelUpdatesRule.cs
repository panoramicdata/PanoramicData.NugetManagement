using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks whether explicitly versioned NuGet packages are missing minor-level updates.
/// </summary>
public sealed class NuGetMinorLevelUpdatesRule : NuGetPackageUpdateRuleBase
{
	/// <summary>
	/// Initializes a new instance of the <see cref="NuGetMinorLevelUpdatesRule"/> class.
	/// </summary>
	public NuGetMinorLevelUpdatesRule()
	{
	}

	/// <summary>
	/// Initializes a new instance with explicit stores and clock, for tests.
	/// </summary>
	/// <param name="cache">The committed upstream snapshot.</param>
	/// <param name="floors">The estate-learned floors.</param>
	/// <param name="timeProvider">The clock the grace period is measured against.</param>
	public NuGetMinorLevelUpdatesRule(
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
	public NuGetMinorLevelUpdatesRule(
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		TimeProvider timeProvider,
		NuGetOwnedPackageCatalog owned)
		: base(cache, floors, timeProvider, owned)
	{
	}

	/// <inheritdoc />
	public override string RuleId => "PKG-06";

	/// <inheritdoc />
	public override string RuleName => "Minor-level NuGet packages up to date";

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	protected override PackageUpdateLevel TargetUpdateLevel => PackageUpdateLevel.Minor;

	/// <inheritdoc />
	protected override string UpdateLevelDisplayName => "minor-level";

	/// <inheritdoc />
	/// <remarks>
	/// None, for the same reason as build level: a minor release is additive by convention, and if it
	/// is a finding it should arrive with the fix that closes it rather than wait ninety days to
	/// become one. Major is the only level that still waits — see
	/// <see cref="NuGetMajorLevelUpdatesRule"/>, where the update is breaking by definition.
	/// </remarks>
	protected override int GraceDays => 0;
}
