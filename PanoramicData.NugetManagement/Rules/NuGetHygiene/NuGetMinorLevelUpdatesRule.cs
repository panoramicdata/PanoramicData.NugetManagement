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
	/// Initializes a new instance of the <see cref="NuGetMinorLevelUpdatesRule"/> class.
	/// </summary>
	/// <param name="versionStatusResolver">Resolves the latest version status for a package.</param>
	public NuGetMinorLevelUpdatesRule(Func<string, string, CancellationToken, Task<PackageVersionStatus?>> versionStatusResolver)
		: base(versionStatusResolver)
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
}
