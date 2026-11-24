using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services.DocComments;

/// <summary>
/// What the build after insertion says.
/// </summary>
/// <param name="Passed">Whether it is safe to keep the edits.</param>
/// <param name="Complete">Whether nothing is left undocumented as well.</param>
/// <param name="RemainingMissing">How many CS1591 diagnostics are still reported.</param>
/// <param name="Reason">Why it did not pass, or an empty string.</param>
public sealed partial record DocBuildVerdict(bool Passed, bool Complete, int RemainingMissing, string Reason)
{
	// The documentation diagnostics a bad comment can cause. Anything here other than CS1591 means an edit broke something.
	private static readonly HashSet<string> _docCodes =
	[
		"CS1570", "CS1571", "CS1572", "CS1573", "CS1574", "CS1580", "CS1581", "CS1584", "CS1587", "CS1589",
		"CS1590", "CS1592", "CS1710", "CS1711", "CS1712", "CS1723", "CS1734"
	];

	/// <summary>
	/// Judges the output of a build made with warnings not treated as errors.
	/// </summary>
	/// <param name="buildOutput">The text of the build.</param>
	/// <param name="missingBefore">How many CS1591 diagnostics the build before insertion reported.</param>
	/// <remarks>
	/// A clean-looking build is not proof on its own: CS1584 is an error, and the compiler stops emitting
	/// documentation diagnostics once it hits one, so the output must also say the build succeeded.
	/// </remarks>
	public static DocBuildVerdict Judge(string buildOutput, int missingBefore)
	{
		var remaining = Cs1591Parser.Parse(buildOutput).Count;
		var matches = DiagnosticPattern().Matches(buildOutput);
		var docProblem = matches.Select(m => m.Groups["code"].Value).FirstOrDefault(_docCodes.Contains);

		string? failure = null;

		if (!buildOutput.Contains("Build succeeded", StringComparison.Ordinal))
		{
			failure = "the build did not report success";
		}
		else if (matches.Any(m => m.Groups["sev"].Value == "error"))
		{
			failure = "the build reported errors";
		}
		else if (docProblem is not null)
		{
			failure = $"a documentation diagnostic appeared: {docProblem}";
		}
		else if (missingBefore > 0 && remaining >= missingBefore)
		{
			failure = "no member was documented";
		}

		return failure is null
			? new DocBuildVerdict(true, remaining == 0, remaining, string.Empty)
			: new DocBuildVerdict(false, false, remaining, failure);
	}

	[GeneratedRegex(@"(?<sev>warning|error) (?<code>CS\d{4}):", RegexOptions.CultureInvariant)]
	private static partial Regex DiagnosticPattern();
}
