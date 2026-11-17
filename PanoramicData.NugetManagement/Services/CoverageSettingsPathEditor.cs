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
/// <para>
/// The path is relative to the step's <c>working-directory</c> when it has one, so it is resolved
/// against that before the workspace is prefixed. A path that climbs out of the repository is left alone.
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
			if (RewriteLine(lines, i) is { } rewritten)
			{
				lines[i] = rewritten;
				changed = true;
			}
		}

		return changed ? string.Join(newline, lines) : null;
	}

	private static string? RewriteLine(string[] lines, int i)
	{
		var line = lines[i];

		// A comment can quote the flag — the workflows this fixes carry one explaining the problem.
		if (line.TrimStart().StartsWith('#'))
		{
			return null;
		}

		var (match, prefixed) = MatchSettings(line);
		var pathGroup = match.Groups["path"];
		if (!match.Success || IsDriveLetterPath(pathGroup.Value) || !IsDirectHostRun(lines, i))
		{
			return null;
		}

		if (ResolveTarget(lines, i, pathGroup.Value, prefixed) is not { } path)
		{
			return null;
		}

		return string.Concat(
			line.AsSpan(0, pathGroup.Index),
			prefixed ? path : $"{_workspace}/{path}",
			line.AsSpan(pathGroup.Index + pathGroup.Length));
	}

	private static (Match Match, bool Prefixed) MatchSettings(string line)
	{
		var match = SettingsPattern().Match(line);
		if (match.Success)
		{
			return (match, false);
		}

		match = PrefixedPattern().Match(line);
		return (match, match.Success);
	}

	private static string? ResolveTarget(string[] lines, int i, string matchedPath, bool prefixed)
	{
		var path = matchedPath.StartsWith("./", StringComparison.Ordinal) ? matchedPath[2..] : matchedPath;

		var workingDirectory = WorkingDirectory(lines, i);
		if (workingDirectory is not null && (!prefixed || path.StartsWith("../", StringComparison.Ordinal)))
		{
			return Resolve(workingDirectory, path);
		}

		return prefixed ? null : path;
	}

	/// <summary>
	/// Joins a path to a working directory and removes the <c>..</c> segments, or returns null when the
	/// result would leave the repository.
	/// </summary>
	private static string? Resolve(string workingDirectory, string path)
	{
		var segments = new List<string>();
		foreach (var segment in $"{workingDirectory}/{path}".Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			if (segment == ".")
			{
				continue;
			}

			if (segment != "..")
			{
				segments.Add(segment);
			}
			else if (segments.Count == 0)
			{
				return null;
			}
			else
			{
				segments.RemoveAt(segments.Count - 1);
			}
		}

		return segments.Count == 0 ? null : string.Join('/', segments);
	}

	/// <summary>
	/// The <c>working-directory</c> of the step that holds <paramref name="argumentLine"/>, or null.
	/// </summary>
	private static string? WorkingDirectory(string[] lines, int argumentLine)
	{
		var start = argumentLine;
		while (start > 0 && !StepStartPattern().IsMatch(lines[start]))
		{
			start--;
		}

		for (var j = start; j < lines.Length; j++)
		{
			if (j > start && StepStartPattern().IsMatch(lines[j]))
			{
				break;
			}

			if (WorkingDirectoryPattern().Match(lines[j]) is { Success: true } match)
			{
				return NormaliseDirectory(match.Groups["dir"].Value);
			}
		}

		return null;
	}


	private static string? NormaliseDirectory(string raw)
	{
		var value = raw.Trim().Trim('\'', '"').Trim('/');
		return value.Length == 0 || value.Contains("${{", StringComparison.Ordinal) || (value.StartsWith('.') && value.Length == 1)
			? null
			: value;
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

	// A path an earlier run of this editor prefixed with the workspace; only a climbing one can be wrong.
	[GeneratedRegex(@"--coverage-settings\s+\$\{\{ github\.workspace \}\}/(?<path>\.\./[^\s""']*)")]
	private static partial Regex PrefixedPattern();

	[GeneratedRegex(@"^\s*(?:-\s+)?working-directory:\s*(?<dir>\S.*?)\s*$")]
	private static partial Regex WorkingDirectoryPattern();

	[GeneratedRegex(@"^\s*(?:-\s+)?run:")]
	private static partial Regex RunPattern();

	[GeneratedRegex(@"^\s*-\s")]
	private static partial Regex StepStartPattern();

	[GeneratedRegex(@"\bdotnet\s+test\b")]
	private static partial Regex DotnetTestPattern();
}
