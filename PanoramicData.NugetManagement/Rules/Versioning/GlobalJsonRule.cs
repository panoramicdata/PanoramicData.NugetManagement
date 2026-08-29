using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that global.json exists with an SDK pin.
/// <para>
/// The pin is the feature-band floor for the target major version, not the newest SDK on the
/// machine running this tool. Pinning the latter makes one machine's install list a build
/// requirement for every other machine, and <c>rollForward</c> never rolls down.
/// </para>
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
		// The opt-in belongs only to repositories that can run on Microsoft.Testing.Platform. Handing
		// it to one still on xunit v2 and VSTest leaves `dotnet test` unable to run anything, which is
		// what this rule did to several repositories before the condition was added.
		var includeTestRunner = UsesMicrosoftTestingPlatform(context);

		var content = context.GetFileContent("global.json");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"global.json not found at repository root.",
				new RuleAdvisory
				{
					Summary = $"Create global.json pinning SDK version to {Standards.DotNetSdkPinVersion} with rollForward: latestMinor.",
					Detail = $"No `global.json` file was found at the repository root. Create one pinning the SDK version to `{Standards.DotNetSdkPinVersion}` with `rollForward: latestMinor`.",
					Data = new()
					{
						["expected_path"] = "global.json",
						["latest_sdk"] = Standards.DotNetSdkPinVersion,
						["template_content"] = Standards.GetGlobalJsonContent(includeTestRunner)
					}
				}));
		}

		return Task.FromResult(Contains(content, Standards.DotNetSdkPinVersion)
			? Pass($"global.json found with SDK version {Standards.DotNetSdkPinVersion}.")
			: Fail(
				$"global.json does not reference SDK version {Standards.DotNetSdkPinVersion}.",
				new RuleAdvisory
				{
					Summary = $"Update the sdk.version in global.json to {Standards.DotNetSdkPinVersion}.",
					Detail = $"The `global.json` file does not reference SDK version `{Standards.DotNetSdkPinVersion}`. Update the `sdk.version` property.",
					Data = new()
					{
						["file"] = "global.json",
						["latest_sdk"] = Standards.DotNetSdkPinVersion,
						// Sets the one property rather than rewriting the file. Replacing it wholesale
						// discarded whatever else the repository kept there — msbuild-sdks, a test runner
						// it had already configured — none of which this rule has an opinion on.
						["remediation_type"] = "ensure_json_property",
						["property_path"] = "sdk.version",
						["property_value"] = Standards.DotNetSdkPinVersion
					}
				}));
	}
}
