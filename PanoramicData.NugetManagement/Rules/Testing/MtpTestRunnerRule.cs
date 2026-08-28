using System.Text.Json;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that global.json opts <c>dotnet test</c> into the Microsoft.Testing.Platform runner.
/// Without it, a repository on xunit.v3 4.x cannot run its tests at all on the .NET 10 SDK: the
/// VSTest bridge has been removed from Microsoft.Testing.Platform, so <c>dotnet test</c> fails
/// outright.
/// </summary>
public class MtpTestRunnerRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TST-06";

	/// <inheritdoc />
	public override string RuleName => "global.json declares the Microsoft.Testing.Platform test runner";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	private const string _detail = """
		`xunit.v3` 4.x depends on `Microsoft.Testing.Platform` 2.3 or later, which removed its VSTest
		bridge on the .NET 10 SDK. Without the opt-in, `dotnet test` fails before running anything:

		```
		error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on
		.NET 10 SDK and later. If you use dotnet test, you should opt-in to the new dotnet test
		experience.
		```

		The opt-in lives in `global.json`, and only there:

		```json
		{
		  "sdk": { "version": "10.0.400", "rollForward": "latestFeature" },
		  "test": { "runner": "Microsoft.Testing.Platform" }
		}
		```

		Two alternatives look right and are silently ignored on this SDK, so neither is worth trying:

		- `dotnet.config` with `[dotnet.test.runner]` / `name = "Microsoft.Testing.Platform"` at the
		  repository root.
		- The `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` MSBuild
		  property in the test project.

		The gate is `_SupportsGlobalJsonTestRunner` in `Microsoft.Testing.Platform.MSBuild.targets`,
		which consults `global.json` alone — hence the property name.

		The value must be exactly `Microsoft.Testing.Platform`. `MicrosoftTestingPlatform` — the
		internal identifier, and the spelling that appears in the SDK assembly strings — parses but is
		rejected at runtime with `Test runner 'MicrosoftTestingPlatform' is not supported.`
		""";

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var testProjects = context.FindTestProjectFiles().ToList();
		if (testProjects.Count == 0)
		{
			return Task.FromResult(NotApplicable("No test projects found; rule does not apply."));
		}

		// Only xunit.v3 repositories run on Microsoft.Testing.Platform. Declaring the runner in a
		// repository still on VSTest would leave dotnet test finding nothing to run, so the rule
		// waits for TST-02 to be satisfied first.
		if (!UsesMicrosoftTestingPlatform(context))
		{
			// Not merely inapplicable: an opt-in on a VSTest repository actively stops dotnet test
			// finding anything to run. Governance remediation put these there, and this takes them
			// back out.
			var strandedRunner = ReadTestRunner(context.GetFileContent("global.json") ?? string.Empty);
			return Task.FromResult(strandedRunner is null
				? NotApplicable("No xunit.v3 reference found; the Microsoft.Testing.Platform opt-in does not apply.")
				: Fail(
					$"global.json declares test.runner '{strandedRunner}', but this repository's tests do not run on Microsoft.Testing.Platform, so dotnet test can find nothing to run.",
					new RuleAdvisory
					{
						Summary = "Remove the test.runner opt-in, or migrate the tests to xunit.v3.",
						Detail = """
							The `test.runner` key tells `dotnet test` to use Microsoft.Testing.Platform. This
							repository's tests do not run on it — they are still xunit v2 on VSTest — so the
							opt-in leaves `dotnet test` with nothing it can execute.

							Either remove the key, or migrate the test project to `xunit.v3`, after which the
							opt-in becomes required rather than harmful (see TST-02).

							If a governance remediation added this, removing it is the fix: the SDK pin and the
							test runner are separate decisions, and only the first belongs to every repository.
							""",
						Data = new()
						{
							["remediation_type"] = "remove_json_property",
							["file"] = "global.json",
							["property_path"] = "test",
							["current_value"] = strandedRunner
						}
					}));
		}

		var content = context.GetFileContent("global.json");
		if (string.IsNullOrWhiteSpace(content))
		{
			return Task.FromResult(Fail(
				"global.json not found at repository root, so dotnet test cannot opt into Microsoft.Testing.Platform.",
				new RuleAdvisory
				{
					Summary = $"Create global.json declaring test.runner: {Standards.MtpTestRunnerName}.",
					Detail = _detail,
					Data = new()
					{
						["expected_path"] = "global.json",
						["template_content"] = Standards.GetGlobalJsonContent(includeTestRunner: true),
						["test_runner"] = Standards.MtpTestRunnerName
					}
				}));
		}

		var runner = ReadTestRunner(content);

		if (string.Equals(runner, Standards.MtpTestRunnerName, StringComparison.Ordinal))
		{
			return Task.FromResult(Pass($"global.json declares test.runner: {Standards.MtpTestRunnerName}."));
		}

		var message = runner is null
			? "global.json does not declare test.runner, so dotnet test still targets VSTest and fails on the .NET 10 SDK."
			: $"global.json declares test.runner '{runner}', which the SDK rejects; the only accepted value is '{Standards.MtpTestRunnerName}'.";

		return Task.FromResult(Fail(
			message,
			new RuleAdvisory
			{
				Summary = $"Set test.runner to {Standards.MtpTestRunnerName} in global.json.",
				Detail = _detail,
				Data = new()
				{
					["remediation_type"] = "ensure_json_property",
					["file"] = "global.json",
					["property_path"] = "test.runner",
					["property_value"] = Standards.MtpTestRunnerName,
					["current_value"] = runner ?? string.Empty
				}
			}));
	}

	/// <summary>
	/// Reads <c>test.runner</c> from global.json, returning null when it is absent or the file cannot
	/// be parsed.
	/// </summary>
	private static string? ReadTestRunner(string content)
	{
		try
		{
			using var document = JsonDocument.Parse(
				content,
				new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

			return document.RootElement.ValueKind == JsonValueKind.Object
				&& document.RootElement.TryGetProperty("test", out var test)
				&& test.ValueKind == JsonValueKind.Object
				&& test.TryGetProperty("runner", out var value)
				&& value.ValueKind == JsonValueKind.String
					? value.GetString()
					: null;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
