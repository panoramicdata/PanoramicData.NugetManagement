using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

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
				new RuleAdvisory
				{
					Summary = $"Create global.json pinning SDK version to {Standards.LatestDotNetSdkVersion} with rollForward: latestFeature.",
					Detail = $"No `global.json` file was found at the repository root. Create one pinning the SDK version to `{Standards.LatestDotNetSdkVersion}` with `rollForward: latestFeature`.",
					Data = new()
					{
						["expected_path"] = "global.json",
						["latest_sdk"] = Standards.LatestDotNetSdkVersion,
						["template_content"] = Standards.GlobalJsonContent
					}
				}));
		}

		return Task.FromResult(Contains(content, Standards.LatestDotNetSdkVersion)
			? Pass($"global.json found with SDK version {Standards.LatestDotNetSdkVersion}.")
			: Fail(
				$"global.json does not reference SDK version {Standards.LatestDotNetSdkVersion}.",
				new RuleAdvisory
				{
					Summary = $"Update the sdk.version in global.json to {Standards.LatestDotNetSdkVersion}.",
					Detail = $"The `global.json` file does not reference SDK version `{Standards.LatestDotNetSdkVersion}`. Update the `sdk.version` property.",
                    Data = new()
					{
						["file"] = "global.json",
						["latest_sdk"] = Standards.LatestDotNetSdkVersion,
						["remediation_type"] = "replace_file_content",
						["new_content"] = Standards.GlobalJsonContent
					}
				}));
	}
}
