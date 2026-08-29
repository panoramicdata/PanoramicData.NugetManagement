using System.Text.Json;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that xunit.runner.json exists in each unit test project with <c>failSkips</c> set to <c>true</c>,
/// preventing skipped tests from producing a false-green test run.
/// </summary>
public class FailSkipsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TST-05";

	/// <inheritdoc />
	public override string RuleName => "Unit tests must not skip (failSkips: true)";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var testProjects = context.FindTestProjectFiles().ToList();

		if (testProjects.Count == 0)
		{
			return Task.FromResult(NotApplicable("No test projects found; rule does not apply."));
		}

		var missingOrInvalid = new List<string>();
		var runnerConfigsToFix = new List<string>();

		foreach (var projectFile in testProjects)
		{
			// Derive the directory prefix to locate xunit.runner.json alongside the project file
			var projectDir = System.IO.Path.GetDirectoryName(projectFile)?.Replace('\\', '/').TrimEnd('/') ?? string.Empty;
			var runnerConfigPath = string.IsNullOrEmpty(projectDir)
				? "xunit.runner.json"
				: $"{projectDir}/xunit.runner.json";

			var content = context.GetFileContent(runnerConfigPath);

			if (string.IsNullOrWhiteSpace(content))
			{
				missingOrInvalid.Add($"{projectFile}: xunit.runner.json not found");
				runnerConfigsToFix.Add(runnerConfigPath);
				continue;
			}

			var declared = TryReadFailSkips(content, out var failSkips);
			if (declared && failSkips)
			{
				continue;
			}

			missingOrInvalid.Add($"{projectFile}: failSkips is not set to true");

			// An explicit `false` is a decision somebody made — an integration suite that skips when
			// credentials are absent, say. Flipping it silently is not a fix, so only a config that
			// never mentions failSkips at all is filled in automatically.
			if (!declared && IsJsonObject(content))
			{
				runnerConfigsToFix.Add(runnerConfigPath);
			}
		}

		if (missingOrInvalid.Count == 0)
		{
			return Task.FromResult(Pass("All test projects have xunit.runner.json with failSkips: true."));
		}

		return Task.FromResult(Fail(
			$"One or more test projects are missing failSkips configuration: {string.Join("; ", missingOrInvalid)}",
			new RuleAdvisory
			{
				Summary = "Add xunit.runner.json with failSkips: true to each unit test project to prevent skipped tests from masking failures.",
				Detail = """
					Unit tests must never be skipped silently. A skipped test gives a false sense of security.
					Add an `xunit.runner.json` file alongside each unit test `.csproj` with the following content:

					```json
					{
					  "$schema": "https://xunit.net/schema/current/xunit.runner.schema.json",
					  "failSkips": true
					}
					```

					For integration test projects where skipping is intentional (for example when credentials are absent),
					set `failSkips: false` explicitly and document the reason in a comment or README.
					""",
				Data = BuildData(runnerConfigsToFix, missingOrInvalid.Count)
			}));
	}

	/// <summary>
	/// The config to write where a test project has none.
	/// </summary>
	private const string _runnerConfigTemplate =
		"""
		{
		  "$schema": "https://xunit.net/schema/current/xunit.runner.schema.json",
		  "failSkips": true
		}
		""";

	/// <summary>
	/// Builds the advisory data, attaching a remediation payload only when every offending config can
	/// be fixed without overruling a choice the repository made deliberately. A payload that fixed
	/// some of them would report success while leaving the rule failing.
	/// </summary>
	/// <param name="runnerConfigsToFix">The xunit.runner.json paths that can be written automatically.</param>
	/// <param name="offendingCount">How many test projects violate the rule.</param>
	private static Dictionary<string, object> BuildData(List<string> runnerConfigsToFix, int offendingCount)
	{
		var data = new Dictionary<string, object>
		{
			["runner_configs"] = runnerConfigsToFix.ToArray()
		};

		if (runnerConfigsToFix.Count == offendingCount)
		{
			data["remediation_type"] = "ensure_json_property";
			data["files"] = runnerConfigsToFix.ToArray();
			data["property_path"] = "failSkips";
			data["property_value"] = "true";
			data["value_kind"] = "bool";
			data["create_content"] = _runnerConfigTemplate;
		}

		return data;
	}

	/// <summary>
	/// Whether the content parses as a JSON object, and so can have a property added to it.
	/// </summary>
	private static bool IsJsonObject(string json)
	{
		try
		{
			using var doc = JsonDocument.Parse(json);
			return doc.RootElement.ValueKind == JsonValueKind.Object;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static bool TryReadFailSkips(string json, out bool failSkips)
	{
		failSkips = false;
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("failSkips", out var prop) && prop.ValueKind == JsonValueKind.True)
			{
				failSkips = true;
				return true;
			}

			return doc.RootElement.TryGetProperty("failSkips", out _);
		}
		catch (JsonException)
		{
			return false;
		}
	}
}
