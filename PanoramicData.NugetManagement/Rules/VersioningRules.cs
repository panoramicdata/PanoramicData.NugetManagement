using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that version.json exists for Nerdbank.GitVersioning.
/// </summary>
public class NerdbankVersionJsonRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "VER-01";

	/// <inheritdoc />
	public override string RuleName => "version.json exists";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Versioning;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("version.json");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"version.json not found at repository root.",
				"Create version.json with a version and publicReleaseRefSpec for Nerdbank.GitVersioning."));
		}

		var hasVersion = Contains(content, "\"version\"");
		var hasRefSpec = Contains(content, "publicReleaseRefSpec");

		return Task.FromResult(hasVersion && hasRefSpec
			? Pass("version.json found with version and publicReleaseRefSpec.")
			: Fail(
				"version.json is missing 'version' or 'publicReleaseRefSpec'.",
				"Ensure version.json contains both a 'version' field and 'publicReleaseRefSpec' array."));
	}
}

/// <summary>
/// Checks that Nerdbank.GitVersioning is referenced.
/// </summary>
public class NerdbankPackageReferencedRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "VER-02";

	/// <inheritdoc />
	public override string RuleName => "Nerdbank.GitVersioning referenced";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Versioning;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirPackages = context.GetFileContent("Directory.Packages.props");
		var allCsprojs = context.FindFiles(".csproj")
			.Select(context.GetFileContent)
			.Where(c => c is not null);

		var inCpm = Contains(dirPackages, "Nerdbank.GitVersioning");
		var inCsproj = allCsprojs.Any(c => Contains(c, "Nerdbank.GitVersioning"));

		return Task.FromResult(inCpm || inCsproj
			? Pass("Nerdbank.GitVersioning is referenced.")
			: Fail(
				"Nerdbank.GitVersioning is not referenced in Directory.Packages.props or any .csproj.",
				"Add a PackageVersion for Nerdbank.GitVersioning to Directory.Packages.props."));
	}
}

/// <summary>
/// Checks that global.json exists with the correct SDK version.
/// </summary>
public class GlobalJsonRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "VER-03";

	/// <inheritdoc />
	public override string RuleName => "global.json exists with SDK pin";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Versioning;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var content = context.GetFileContent("global.json");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"global.json not found at repository root.",
				$"Create global.json pinning SDK version to {Standards.LatestDotNetSdkVersion} with rollForward: latestFeature."));
		}

		return Task.FromResult(Contains(content, Standards.LatestDotNetSdkVersion)
			? Pass($"global.json found with SDK version {Standards.LatestDotNetSdkVersion}.")
			: Fail(
				$"global.json does not reference SDK version {Standards.LatestDotNetSdkVersion}.",
				$"Update the sdk.version in global.json to {Standards.LatestDotNetSdkVersion}."));
	}
}
