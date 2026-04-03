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
					Data = new() { ["expected_path"] = "LICENSE", ["expected_license_type"] = context.Options.ExpectedLicense }
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
					Data = new() { ["file"] = "LICENSE", ["expected_license_type"] = context.Options.ExpectedLicense }
				}));
	}
}

/// <summary>
/// Checks that PackageLicenseExpression matches the expected license.
/// </summary>
public class PackageLicenseExpressionRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "LIC-02";

	/// <inheritdoc />
	public override string RuleName => "PackageLicenseExpression matches expected license";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Licensing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var expected = context.Options.ExpectedLicense;
		var expectedTag = $"<PackageLicenseExpression>{expected}</PackageLicenseExpression>";
		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, expectedTag))
			{
				return Task.FromResult(Fail(
					$"{csproj}: PackageLicenseExpression does not match expected \"{expected}\".",
					new RuleAdvisory
					{
						Summary = $"Add {expectedTag} to the .csproj.",
						Detail = $"The project `{csproj}` does not have `PackageLicenseExpression` set to `{expected}`. Add `{expectedTag}` to the project file.",
						Data = new() { ["file"] = csproj, ["expected_license"] = expected }
					}));
			}
		}

		return Task.FromResult(Pass($"All packable projects have PackageLicenseExpression = \"{expected}\"."));
	}
}

/// <summary>
/// Checks that Copyright is set in Directory.Build.props with the expected holder.
/// </summary>
public class CopyrightMessageRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "LIC-03";

	/// <inheritdoc />
	public override string RuleName => "Copyright message in Directory.Build.props";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Licensing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var expected = context.Options.ExpectedCopyrightHolder;
		var content = context.GetFileContent("Directory.Build.props");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"Directory.Build.props not found.",
				new RuleAdvisory
				{
					Summary = $"Create Directory.Build.props with <Copyright> containing \"{expected}\".",
					Detail = $"No `Directory.Build.props` file was found. Create one with `<Copyright>Copyright © $(Year) {expected}</Copyright>`.",
					Data = new() { ["file"] = "Directory.Build.props", ["expected_holder"] = expected }
				}));
		}

		var hasCopyright = Contains(content, "<Copyright>") && Contains(content, expected);
		return Task.FromResult(hasCopyright
			? Pass($"Copyright message found with \"{expected}\".")
			: Fail(
				$"Directory.Build.props does not contain Copyright with expected holder \"{expected}\".",
				new RuleAdvisory
				{
					Summary = $"Add <Copyright>Copyright © $(Year) {expected}</Copyright> to Directory.Build.props.",
					Detail = $"`Directory.Build.props` exists but does not contain a `<Copyright>` element with `{expected}`. Add `<Copyright>Copyright © $(Year) {expected}</Copyright>`.",
					Data = new() { ["file"] = "Directory.Build.props", ["expected_holder"] = expected }
				}));
	}
}
