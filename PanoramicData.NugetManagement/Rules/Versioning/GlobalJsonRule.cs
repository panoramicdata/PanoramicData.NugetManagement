using System.Text.Json.Nodes;
using NuGet.Versioning;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that global.json pins an SDK the repository can actually build with.
/// <para>
/// Two things are checked, and they pull in opposite directions. The version is a <em>floor</em>,
/// not a target: a repository already on a later feature band passes, because <c>rollForward</c>
/// never rolls down and demanding the newest band would make one machine's install list a build
/// requirement for everyone else. Security is no argument for a higher floor either — Microsoft
/// services every live feature band in the same release, so 10.0.111 carries the same fixes as
/// 10.0.400.
/// </para>
/// <para>
/// What the floor needs to mean "a .NET N SDK" is a <c>rollForward</c> that can cross feature
/// bands. Absent it the default is <c>latestPatch</c>, which stays inside the pinned band: a
/// repository floored at 10.0.100 then refuses to build on a machine whose only SDK is 10.0.400.
/// That was the real hole — the old rule matched the pinned version as a substring and never
/// looked at <c>rollForward</c> at all.
/// </para>
/// </summary>
public class GlobalJsonRule : RuleBase
{
	/// <summary>
	/// The <c>rollForward</c> values that can reach an SDK in a higher feature band. The others —
	/// <c>patch</c>, <c>feature</c>, <c>latestPatch</c>, <c>latestFeature</c> — and <c>disable</c>
	/// cannot, which defeats the point of pinning a floor.
	/// </summary>
	private static readonly string[] _bandCrossingRollForward = ["minor", "major", "latestMinor", "latestMajor"];

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
					Summary = $"Create global.json pinning SDK version to {Standards.DotNetSdkPinVersion} with rollForward: {Standards.SdkRollForward}.",
					Detail = $"No `global.json` file was found at the repository root. Create one pinning the SDK version to `{Standards.DotNetSdkPinVersion}` with `rollForward: {Standards.SdkRollForward}`.",
					Data = new()
					{
						["expected_path"] = "global.json",
						["latest_sdk"] = Standards.DotNetSdkPinVersion,
						["template_content"] = Standards.GetGlobalJsonContent(includeTestRunner)
					}
				}));
		}

		var sdk = ParseSdkObject(content);
		if (sdk is null)
		{
			// Nothing in the file can be preserved if it does not parse, so this is the one case that
			// legitimately replaces the whole thing.
			return Task.FromResult(Fail(
				"global.json is not valid JSON.",
				new RuleAdvisory
				{
					Summary = "Replace the unparseable global.json with the standard template.",
					Detail = "The `global.json` file could not be parsed as JSON, so no SDK pin can be read from it.",
					Data = new()
					{
						["remediation_type"] = "replace_file_content",
						["file"] = "global.json",
						["new_content"] = Standards.GetGlobalJsonContent(includeTestRunner)
					}
				}));
		}

		var floor = NuGetVersion.Parse(Standards.DotNetSdkPinVersion);
		var version = sdk["version"]?.ToString();
		var rollForward = sdk["rollForward"]?.ToString();

		// Only what is actually wrong is remediated. A repository already above the floor keeps its
		// version — rewriting it would move it down a feature band for no reason.
		var corrections = new Dictionary<string, string>();
		var reasons = new List<string>();

		if (version is null || !NuGetVersion.TryParse(version, out var pinned))
		{
			corrections["sdk.version"] = Standards.DotNetSdkPinVersion;
			reasons.Add($"no SDK version is pinned (expected at least {Standards.DotNetSdkPinVersion})");
		}
		else if (pinned < floor)
		{
			corrections["sdk.version"] = Standards.DotNetSdkPinVersion;
			reasons.Add($"the pinned SDK version {version} is below the {Standards.DotNetSdkPinVersion} floor");
		}

		if (rollForward is null)
		{
			corrections["sdk.rollForward"] = Standards.SdkRollForward;
			reasons.Add($"rollForward is not set, so it defaults to latestPatch and cannot leave the pinned feature band");
		}
		else if (!_bandCrossingRollForward.Contains(rollForward, StringComparer.OrdinalIgnoreCase))
		{
			corrections["sdk.rollForward"] = Standards.SdkRollForward;
			reasons.Add($"rollForward '{rollForward}' cannot reach an SDK in a higher feature band");
		}

		if (corrections.Count == 0)
		{
			return Task.FromResult(Pass(
				$"global.json pins SDK {version} (at or above the {Standards.DotNetSdkPinVersion} floor) with rollForward: {rollForward}."));
		}

		var summary = string.Join("; ", corrections.Select(c => $"set {c.Key} to {c.Value}"));

		return Task.FromResult(Fail(
			"global.json " + string.Join(" and ", reasons) + ".",
			new RuleAdvisory
			{
				Summary = $"In global.json, {summary}.",
				Detail = $"The `global.json` file needs correcting: {string.Join("; ", reasons)}. The SDK version is a floor — a repository already on a later feature band is fine — but `rollForward` must be able to cross feature bands, or the pin stops the repository building on a machine with a newer SDK band.",
				Data = new()
				{
					["file"] = "global.json",
					["latest_sdk"] = Standards.DotNetSdkPinVersion,
					// Sets only the properties that are wrong rather than rewriting the file. Replacing
					// it wholesale discarded whatever else the repository kept there — msbuild-sdks, a
					// test runner it had already configured — none of which this rule has an opinion on.
					["remediation_type"] = "ensure_json_properties",
					["properties"] = corrections
				}
			}));
	}

	/// <summary>
	/// Reads the <c>sdk</c> object from global.json, or null if the file does not parse as a JSON
	/// object. A file with no <c>sdk</c> member parses to an empty object, so the missing-version
	/// and missing-rollForward paths handle it.
	/// </summary>
	private static JsonObject? ParseSdkObject(string content)
	{
		try
		{
			var node = JsonNode.Parse(
				content,
				documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });

			return node is JsonObject root
				? root["sdk"] as JsonObject ?? []
				: null;
		}
		catch (System.Text.Json.JsonException)
		{
			return null;
		}
	}
}
