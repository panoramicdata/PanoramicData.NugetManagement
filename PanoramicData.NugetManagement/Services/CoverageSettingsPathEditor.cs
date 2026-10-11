using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Makes a relative <c>--coverage-settings</c> path absolute in a workflow's direct test-host runs.
/// </summary>
/// <remarks>
/// Microsoft.Testing.Extensions.CodeCoverage 18.12 does not resolve a relative settings path when the
/// test application is run directly: it stops with "Provided settings file doesn't exist or is
/// invalid", the coverage job fails before it uploads anything, and Codacy holds no figure. Under
/// <c>dotnet test</c> the same relative path works, so those steps are left alone — both were run.
/// <para>
/// A direct run is told apart from <c>dotnet test</c> by reading from the step's <c>run:</c> line down to
/// the argument, because the arguments sit on their own lines of a folded block. Quoted values and
/// anything already absolute are not touched.
/// </para>
/// </remarks>
public static partial class CoverageSettingsPathEditor
{
	private const string _workspace = "${{ github.workspace }}";

	/// <summary>
	/// Rewrites every relative settings path in a direct test-host run.
	/// </summary>
	/// <param name="workflow">The workflow file's text.</param>
	/// <returns>The edited workflow, or null when nothing needed changing.</returns>
	public static string? MakeAbsolute(string workflow)
	{
		var newline = workflow.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var lines = workflow.Replace("\r\n", "\n").Split('\n');
		var changed = false;

		for (var i = 0; i < lines.Length; i++)
		{
			// A comment can quote the flag — the workflows this fixes carry one explaining the problem.
			if (lines[i].TrimStart().StartsWith('#'))
			{
				continue;
			}

			var match = SettingsPattern().Match(lines[i]);
			if (!match.Success || IsDriveLetterPath(match.Groups["path"].Value) || !IsDirectHostRun(lines, i))
			{
				continue;
			}

			var path = match.Groups["path"].Value;
			if (path.StartsWith("./", StringComparison.Ordinal))
			{
				path = path[2..];
			}

			lines[i] = string.Concat(
				lines[i].AsSpan(0, match.Groups["path"].Index),
				$"{_workspace}/{path}",
				lines[i].AsSpan(match.Groups["path"].Index + match.Groups["path"].Length));
			changed = true;
		}

		return changed ? string.Join(newline, lines) : null;
	}

	/// <summary>
	/// Whether the step holding line <paramref name="argumentLine"/> runs something other than
	/// <c>dotnet test</c>.
	/// </summary>
	private static bool IsDirectHostRun(string[] lines, int argumentLine)
	{
		for (var j = argumentLine; j >= 0; j--)
		{
			if (RunPattern().IsMatch(lines[j]))
			{
				var command = string.Join(' ', lines[j..(argumentLine + 1)]);
				return !DotnetTestPattern().IsMatch(command);
			}

			// A list item reached before any run: key is a step that is not a run step.
			if (j < argumentLine && StepStartPattern().IsMatch(lines[j]))
			{
				return false;
			}
		}

		return false;
	}

	private static bool IsDriveLetterPath(string path)
		=> path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

	// A bare path: not quoted, not a variable or expression, not rooted, not home-relative.
	[GeneratedRegex(@"--coverage-settings\s+(?<path>[^\s""'$/~\\][^\s""']*)")]
	private static partial Regex SettingsPattern();

	[GeneratedRegex(@"^\s*(?:-\s+)?run:")]
	private static partial Regex RunPattern();

	[GeneratedRegex(@"^\s*-\s")]
	private static partial Regex StepStartPattern();

	[GeneratedRegex(@"\bdotnet\s+test\b")]
	private static partial Regex DotnetTestPattern();
}
