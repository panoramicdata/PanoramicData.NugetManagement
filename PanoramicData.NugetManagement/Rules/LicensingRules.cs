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
				$"Create a LICENSE file containing {expectedText} text."));
		}

		return Task.FromResult(Contains(content, expectedText)
			? Pass($"LICENSE file contains expected text \"{expectedText}\".")
			: Fail(
				$"LICENSE file does not contain expected text \"{expectedText}\".",
				$"Replace LICENSE content with the standard {context.Options.ExpectedLicense} License text."));
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
					$"Add {expectedTag} to the .csproj."));
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
				$"Create Directory.Build.props with <Copyright> containing \"{expected}\"."));
		}

		var hasCopyright = Contains(content, "<Copyright>") && Contains(content, expected);
		return Task.FromResult(hasCopyright
			? Pass($"Copyright message found with \"{expected}\".")
			: Fail(
				$"Directory.Build.props does not contain Copyright with expected holder \"{expected}\".",
				$"Add <Copyright>Copyright © $(Year) {expected}</Copyright> to Directory.Build.props."));
	}
}
