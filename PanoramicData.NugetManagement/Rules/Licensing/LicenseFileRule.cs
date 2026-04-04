using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a LICENSE file exists with the expected license content.
/// </summary>
public class LicenseFileRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "LIC-01";

	/// <inheritdoc />
	public override string RuleName => "LICENSE file contains expected license text";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Licensing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var expectedText = context.Options.GetExpectedLicenseFileText();
		var content = context.GetFileContent("LICENSE");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"LICENSE file not found at repository root.",
				new RuleAdvisory
				{
					Summary = $"Create a LICENSE file containing {expectedText} text.",
					Detail = $"No `LICENSE` file was found at the repository root. Create one containing the standard {context.Options.ExpectedLicense} license text.",
					Data = new()
					{
						["expected_path"] = "LICENSE",
						["template_content"] = Standards.MitLicenseContent,
						["expected_license_type"] = context.Options.ExpectedLicense
					}
				}));
		}

		return Task.FromResult(Contains(content, expectedText)
			? Pass($"LICENSE file contains expected text \"{expectedText}\".")
			: Fail(
				$"LICENSE file does not contain expected text \"{expectedText}\".",
				new RuleAdvisory
				{
					Summary = $"Replace LICENSE content with the standard {context.Options.ExpectedLicense} License text.",
					Detail = $"The `LICENSE` file exists but does not contain the expected text `{expectedText}`. Replace its content with the standard {context.Options.ExpectedLicense} license text.",
					Data = new()
					{
						["file"] = "LICENSE",
						["remediation_type"] = "replace_file_content",
						["new_content"] = Standards.MitLicenseContent,
						["expected_license_type"] = context.Options.ExpectedLicense
					}
				}));
	}
}
