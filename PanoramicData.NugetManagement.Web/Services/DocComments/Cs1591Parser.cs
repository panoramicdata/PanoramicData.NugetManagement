using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services.DocComments;

/// <summary>
/// One public member the compiler says has no XML documentation comment.
/// </summary>
/// <param name="FilePath">The source file, as the compiler printed it.</param>
/// <param name="Line">The 1-based line of the declaration.</param>
/// <param name="Column">The 1-based column.</param>
/// <param name="Member">The member's compiler display name.</param>
public sealed record MissingDocDiagnostic(string FilePath, int Line, int Column, string Member);

/// <summary>
/// Reads CS1591 diagnostics out of MSBuild output.
/// </summary>
public static partial class Cs1591Parser
{
	// Anchored to line start with a path that cannot span lines: MSBuild wraps long diagnostics and
	// prefixes parallel node output with "1>", and an unanchored pattern swallows the preceding tail.
	[GeneratedRegex(
		@"^\s*(?:\d+>)?(?<file>(?:[A-Za-z]:[\\/]|/)[^()\r\n]+?\.cs)\((?<line>\d+),(?<col>\d+)\):\s*(?:warning|error) CS1591: Missing XML comment for publicly visible type or member '(?<key>[^'\r\n]+)'",
		RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex DiagnosticPattern();

	/// <summary>
	/// The distinct missing-comment diagnostics in a build's output.
	/// </summary>
	/// <param name="buildOutput">The text of a build.</param>
	public static IReadOnlyList<MissingDocDiagnostic> Parse(string buildOutput)
		=> [
			.. DiagnosticPattern().Matches(buildOutput)
				.Select(m => new MissingDocDiagnostic(
					m.Groups["file"].Value,
					int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
					int.Parse(m.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture),
					m.Groups["key"].Value))
				.Distinct()
		];
}
