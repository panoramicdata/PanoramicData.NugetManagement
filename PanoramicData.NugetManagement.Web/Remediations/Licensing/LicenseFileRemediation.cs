using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Web.Remediations.Licensing;

/// <summary>Creates or replaces LICENSE file.</summary>
public sealed class LicenseFileRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "LIC-01";

	/// <inheritdoc />
	/// <remarks>
	/// The only text this can write is MIT, so it refuses any advisory for another licence, including
	/// one cached from before the rule stopped offering the fix.
	/// </remarks>
	public override bool CanRemediate(RuleResult result)
		=> base.CanRemediate(result)
			&& ReadStrings(result.Advisory!.Data, "expected_license_type") is var licenses
			&& (licenses.Length == 0 || LicenseExpressions.IsDefault(licenses[0]));
}
