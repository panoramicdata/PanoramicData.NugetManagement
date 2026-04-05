using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that README.md contains a license badge.
/// </summary>
public class LicenseBadgeRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "README-04";

	/// <inheritdoc />
	public override string RuleName => "License badge in README";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ReadmeBadges;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var license = context.Options.ExpectedLicense;
		var content = context.GetFileContent("README.md");
		return Task.FromResult(
			Contains(content, $"License: {license}") || Contains(content, $"license-{license}")
			? Pass($"License badge for \"{license}\" found in README.md.")
			: Fail(
				$"README.md does not contain a license badge for \"{license}\".",
				new RuleAdvisory
				{
					Summary = $"Add a license badge (e.g. shields.io {license} badge) at the top of README.md.",
					Detail = $"The `README.md` does not contain a license badge for `{license}`. Add a shields.io badge such as `[![License: {license}](https://img.shields.io/badge/License-{license}-yellow.svg)](https://opensource.org/licenses/{license})`.",
					Data = new()
					{
						["file"] = "README.md",
						["expected_license"] = license,
						["remediation_type"] = "prepend_line",
						["line_content"] = $"[![License: {license}](https://img.shields.io/badge/License-{license}-yellow.svg)](https://opensource.org/licenses/{license})"
					}
				}));
	}
}
