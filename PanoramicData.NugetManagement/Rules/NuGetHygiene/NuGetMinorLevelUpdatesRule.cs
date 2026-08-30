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
	protected override int GraceDays => 90;
}
