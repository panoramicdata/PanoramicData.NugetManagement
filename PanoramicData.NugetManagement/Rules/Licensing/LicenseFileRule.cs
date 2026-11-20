using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a LICENSE file exists with the expected license content.
/// </summary>
/// <remarks>
/// Only the default licence has standard text to write, so only it carries a fix; any other licence
/// fails without one rather than having its LICENSE overwritten with MIT.
/// </remarks>
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
		var license = context.Options.ExpectedLicense;
		var expectedTexts = context.Options.GetExpectedLicenseFileTexts();
		var expectedText = string.Join("\" and \"", expectedTexts);
		var canWriteText = LicenseExpressions.IsDefault(license);
		var content = context.GetFileContent("LICENSE");
		if (content is null)
		{
			var data = new Dictionary<string, object> { ["expected_license_type"] = license };
			if (canWriteText)
			{
				data["expected_path"] = "LICENSE";
				data["template_content"] = Standards.MitLicenseContent;
			}

			return Task.FromResult(Fail(
				"LICENSE file not found at repository root.",
				new RuleAdvisory
				{
					Summary = $"Create a LICENSE file containing \"{expectedText}\".",
					Detail = $"No `LICENSE` file was found at the repository root. Create one containing the full {license} license text.",
					Data = data
				}));
		}

		var missing = expectedTexts.Where(text => !Contains(content, text)).ToList();
		if (missing.Count == 0)
		{
			return Task.FromResult(Pass($"LICENSE file contains expected text \"{expectedText}\"."));
		}

		var failData = new Dictionary<string, object>
		{
			["file"] = "LICENSE",
			["expected_license_type"] = license
		};
		if (canWriteText)
		{
			failData["remediation_type"] = "replace_file_content";
			failData["new_content"] = Standards.MitLicenseContent;
		}

		return Task.FromResult(Fail(
			$"LICENSE file does not contain expected text \"{string.Join("\" and \"", missing)}\".",
			new RuleAdvisory
			{
				Summary = $"Replace LICENSE content with the full {license} license text.",
				Detail = $"The `LICENSE` file exists but does not contain the expected text `{string.Join("`, `", missing)}`. Replace its content with the full {license} license text, or set the repository's licence override if {license} is not its licence.",
				Data = failData
			}));
	}
}
