using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that README.md exists and is non-trivial.
/// </summary>
public class ReadmeExistsRule : RuleBase
{
	private const int _minReadmeLength = 200;

	/// <inheritdoc />
	public override string RuleId => "README-01";

	/// <inheritdoc />
	public override string RuleName => "README.md exists and is comprehensive";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ReadmeBadges;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("README.md");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"README.md not found at repository root.",
				new RuleAdvisory
				{
					Summary = "Create a comprehensive README.md with badges, introduction, installation, and usage sections.",
					Detail = "No `README.md` file was found at the repository root. Create one with badges, introduction, installation, and usage sections.",
					Data = new() { ["expected_path"] = "README.md" }
				}));
		}

		return Task.FromResult(content.Length >= _minReadmeLength
			? Pass($"README.md found ({content.Length} characters).")
			: Fail(
				$"README.md is too short ({content.Length} characters, minimum {_minReadmeLength}).",
				new RuleAdvisory
				{
					Summary = "Expand README.md with introduction, installation, usage, and examples sections.",
					Detail = $"The `README.md` is only {content.Length} characters (minimum required: {_minReadmeLength}). Expand it with introduction, installation, usage, and examples sections.",
					Data = new() { ["file"] = "README.md", ["actual_length"] = content.Length, ["min_length"] = _minReadmeLength }
				}));
	}
}

/// <summary>
/// Checks that README.md contains a Codacy badge.
/// </summary>
public class CodacyBadgeRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "README-02";

	/// <inheritdoc />
	public override string RuleName => "Codacy badge in README";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ReadmeBadges;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("README.md");
		return Task.FromResult(Contains(content, "codacy")
			? Pass("Codacy badge found in README.md.")
			: Fail(
				"README.md does not contain a Codacy badge.",
				new RuleAdvisory
				{
					Summary = "Add a Codacy badge link (from app.codacy.com) at the top of README.md.",
					Detail = "The `README.md` does not contain a Codacy badge. Add a Codacy badge link from `app.codacy.com` at the top of the file.",
					Data = new() { ["file"] = "README.md" }
				}));
	}
}

/// <summary>
/// Checks that README.md contains a NuGet version badge.
/// </summary>
public class NuGetVersionBadgeRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "README-03";

	/// <inheritdoc />
	public override string RuleName => "NuGet version badge in README";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ReadmeBadges;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var content = context.GetFileContent("README.md");
		return Task.FromResult(Contains(content, "nuget.org/packages")
			? Pass("NuGet version badge found in README.md.")
			: Fail(
				"README.md does not contain a NuGet version badge.",
				new RuleAdvisory
				{
					Summary = "Add a NuGet version badge linking to nuget.org/packages/<PackageId>.",
					Detail = "The `README.md` does not contain a NuGet version badge. Add a badge linking to `nuget.org/packages/<PackageId>`.",
					Data = new() { ["file"] = "README.md" }
				}));
	}
}

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
					Data = new() { ["file"] = "README.md", ["expected_license"] = license }
				}));
	}
}
