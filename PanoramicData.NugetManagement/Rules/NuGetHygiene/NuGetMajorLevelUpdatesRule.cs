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
	/// Initializes a new instance with explicit stores and clock, for tests.
	/// </summary>
	/// <param name="cache">The committed upstream snapshot.</param>
	/// <param name="floors">The estate-learned floors.</param>
	/// <param name="timeProvider">The clock the grace period is measured against.</param>
	public NuGetMajorLevelUpdatesRule(
		NuGetVersionCache cache,
		NuGetFloorCatalog floors,
		TimeProvider timeProvider)
		: base(cache, floors, timeProvider)
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

	/// <inheritdoc />
	protected override int GraceDays => 365;
}
