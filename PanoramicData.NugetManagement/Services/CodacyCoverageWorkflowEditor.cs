using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// How a repository's tests are run, which decides the flags that make them write coverage.
/// </summary>
public enum CoverageRunner
{
	/// <summary>Microsoft.Testing.Platform, with the Microsoft.Testing.Extensions.CodeCoverage extension.</summary>
	MicrosoftTestingPlatform,

	/// <summary>VSTest, with the coverlet.collector data collector.</summary>
	VsTest
}

/// <summary>
/// Edits a GitHub Actions workflow so its <c>dotnet test</c> step writes a Cobertura report and a
/// following step uploads it to Codacy.
/// </summary>
/// <remarks>
/// The contract is all or nothing: <see cref="AddUpload"/> returns a complete edited workflow, or null
/// when it cannot be sure of the edit, and never a half-edit. That matters because CI-15 treats a
/// workflow that mentions the Codacy reporter as passing — an edit that left the step unable to run
/// would turn a visible failure into a silent one.
/// <para>
/// Both flag sets were run, not assumed: the Microsoft.Testing.Platform flags write
/// <c>TestResults/coverage.cobertura.xml</c> and the VSTest collector writes
/// <c>TestResults/{guid}/coverage.cobertura.xml</c>, so one glob finds either. The action accepts a
/// glob relative to the repository root.
/// </para>
/// </remarks>
public static partial class CodacyCoverageWorkflowEditor
{
	private const string _mtpFlags = "--coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml";

	private const string _vsTestFlags = "--collect:\"XPlat Code Coverage\"";

	private const string _uploadStepName = "Upload coverage to Codacy";

	/// <summary>
	/// Adds coverage collection to the workflow's test step and an upload step after it.
	/// </summary>
	/// <param name="workflow">The workflow file's text.</param>
	/// <param name="runner">How the repository runs its tests.</param>
	/// <returns>The edited workflow, or null when the workflow cannot be edited safely.</returns>
	public static string? AddUpload(string workflow, CoverageRunner runner)
	{
		var newline = workflow.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var lines = workflow.Replace("\r\n", "\n").Split('\n').ToList();

		if (workflow.Contains(_uploadStepName, StringComparison.Ordinal))
		{
			return null;
		}

		var run = lines.FindIndex(line => TestStepPattern().IsMatch(line));
		if (run < 0)
		{
			return null;
		}

		var match = TestStepPattern().Match(lines[run]);
		var command = match.Groups["command"].Value.TrimEnd();

		// Someone has already chosen how coverage is collected, and a second set of flags would fight it.
		if (CoverageFlagPattern().IsMatch(command))
		{
			return null;
		}

		var stepStart = FindStepStart(lines, run);
		if (stepStart < 0 || !RunsOnSupportedRunner(lines, stepStart))
		{
			return null;
		}

		var dashIndent = IndentOf(lines[stepStart]);
		var stepEnd = FindStepEnd(lines, run, dashIndent);
		var padding = new string(' ', dashIndent);

		lines[run] = $"{lines[run][..match.Groups["command"].Index]}{command} {(runner == CoverageRunner.MicrosoftTestingPlatform ? _mtpFlags : _vsTestFlags)}";

		string[] upload =
		[
			string.Empty,
			$"{padding}- name: {_uploadStepName}",
			$"{padding}  continue-on-error: true",
			$"{padding}  uses: codacy/codacy-coverage-reporter-action@v1",
			$"{padding}  with:",
			$"{padding}    project-token: ${{{{ secrets.CODACY_PROJECT_TOKEN }}}}",
			$"{padding}    coverage-reports: '**/coverage.cobertura.xml'"
		];

		lines.InsertRange(stepEnd, upload);

		return string.Join(newline, lines);
	}

	/// <summary>
	/// The line the step containing <paramref name="run"/> starts on: its list dash.
	/// </summary>
	private static int FindStepStart(List<string> lines, int run)
	{
		// "- run: dotnet test" puts the dash on the run line itself.
		if (DashPattern().IsMatch(lines[run]))
		{
			return run;
		}

		var runIndent = IndentOf(lines[run]);
		for (var i = run - 1; i >= 0; i--)
		{
			if (string.IsNullOrWhiteSpace(lines[i]))
			{
				continue;
			}

			if (DashPattern().IsMatch(lines[i]) && IndentOf(lines[i]) < runIndent)
			{
				return i;
			}

			// A line indented less than the step's keys that is not a dash means the step was never found.
			if (IndentOf(lines[i]) < runIndent)
			{
				return -1;
			}
		}

		return -1;
	}

	/// <summary>
	/// The index to insert after the step: just past its last non-blank line, so a blank separator
	/// between steps stays where it was.
	/// </summary>
	private static int FindStepEnd(List<string> lines, int run, int dashIndent)
	{
		var last = run;
		for (var i = run + 1; i < lines.Count; i++)
		{
			if (string.IsNullOrWhiteSpace(lines[i]))
			{
				continue;
			}

			if (IndentOf(lines[i]) <= dashIndent)
			{
				break;
			}

			last = i;
		}

		return last + 1;
	}

	/// <summary>
	/// Whether the job holding the step runs somewhere the Codacy action is documented to work.
	/// </summary>
	/// <remarks>
	/// The action's README shows ubuntu and nothing else, so a Windows or macOS runner is declined
	/// rather than assumed to work.
	/// </remarks>
	private static bool RunsOnSupportedRunner(List<string> lines, int stepStart)
	{
		for (var i = stepStart - 1; i >= 0; i--)
		{
			var match = RunsOnPattern().Match(lines[i]);
			if (match.Success)
			{
				var value = match.Groups["value"].Value;
				return !value.Contains("windows", StringComparison.OrdinalIgnoreCase)
					&& !value.Contains("macos", StringComparison.OrdinalIgnoreCase);
			}
		}

		return false;
	}

	private static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;

	// A single-line "run: dotnet test ..." with no pipe or fold indicator, optionally the first key of a list item.
	[GeneratedRegex(@"^\s*(?:-\s+)?run:\s+(?<command>dotnet test\b[^|>#]*)$")]
	private static partial Regex TestStepPattern();

	[GeneratedRegex(@"^\s*-\s")]
	private static partial Regex DashPattern();

	[GeneratedRegex(@"^\s*runs-on:\s*(?<value>.+?)\s*$")]
	private static partial Regex RunsOnPattern();

	[GeneratedRegex(@"--coverage\b|--collect\b|CollectCoverage|coverlet", RegexOptions.IgnoreCase)]
	private static partial Regex CoverageFlagPattern();
}
