using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that README.md exists and is non-trivial.
/// </summary>
public class ReadmeExistsRule : RuleBase
{
	private const int MinReadmeLength = 200;

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
				"Create a comprehensive README.md with badges, introduction, installation, and usage sections."));
		}

		return Task.FromResult(content.Length >= MinReadmeLength
			? Pass($"README.md found ({content.Length} characters).")
			: Fail(
				$"README.md is too short ({content.Length} characters, minimum {MinReadmeLength}).",
				"Expand README.md with introduction, installation, usage, and examples sections."));
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
				"Add a Codacy badge link (from app.codacy.com) at the top of README.md."));
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
				"Add a NuGet version badge linking to nuget.org/packages/<PackageId>."));
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
				$"Add a license badge (e.g. shields.io {license} badge) at the top of README.md."));
	}
}
