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
				continue;
			}

			if (!TryReadFailSkips(content, out var failSkips) || !failSkips)
			{
				missingOrInvalid.Add($"{projectFile}: failSkips is not set to true");
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
				Data = new()
				{
					["remediation_type"] = "add_file",
					["file"] = "xunit.runner.json",
					["content"] = """{"$schema":"https://xunit.net/schema/current/xunit.runner.schema.json","failSkips":true}"""
				}
			}));
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
