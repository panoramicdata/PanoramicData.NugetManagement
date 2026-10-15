using System.Globalization;
using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// The outcome of a build or test run.
/// </summary>
/// <param name="Passed">Whether it exited successfully.</param>
/// <param name="Output">What it printed.</param>
public sealed record TestRun(bool Passed, string Output);

/// <summary>
/// Whether a test run counts as a reproduction of a reported bug.
/// </summary>
/// <param name="IsValidRed">True when the test failed for the right kind of reason.</param>
/// <param name="Message">What to tell the model when it is not.</param>
public sealed record RedTestVerdict(bool IsValidRed, string Message);

/// <summary>
/// Decides whether a failing test run is a genuine reproduction.
/// </summary>
/// <remarks>
/// A red run is evidence only if the test ran and failed on its assertion. A test that does not
/// compile, or that matched nothing, also exits non-zero, and accepting either would let any bug be
/// "reproduced" by a test that proves nothing.
/// </remarks>
public static partial class RedTestGate
{
	/// <summary>
	/// Judges a run of the new test against the unfixed code.
	/// </summary>
	/// <param name="unfixed">The run of the new test alone, before any fix.</param>
	public static RedTestVerdict Validate(TestRun unfixed)
	{
		if (unfixed.Passed)
		{
			return new RedTestVerdict(
				false,
				"The test passes on the unfixed code, so it does not reproduce the report. "
					+ "Change it so it fails while the bug is present.");
		}

		if (unfixed.Output.Contains("error CS", StringComparison.Ordinal)
			|| unfixed.Output.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase))
		{
			return new RedTestVerdict(
				false,
				"The test does not compile, which fails for every bug and proves none. "
					+ $"Fix the compile errors first:\n{unfixed.Output}");
		}

		if (unfixed.Output.Contains("Zero tests ran", StringComparison.OrdinalIgnoreCase)
			|| unfixed.Output.Contains("total: 0", StringComparison.OrdinalIgnoreCase))
		{
			return new RedTestVerdict(
				false,
				"The run found no tests, so nothing was reproduced. Check the test is in the test "
					+ "project and that its name matches the filter.");
		}

		var total = ReadCount(TotalPattern(), unfixed.Output);
		var failed = ReadCount(FailedPattern(), unfixed.Output);

		if (total is null || failed is null)
		{
			return new RedTestVerdict(
				false,
				"The run's summary (how many tests ran and how many failed) could not be read, so there is "
					+ "no evidence the new test failed on its own. Run only your new test.");
		}

		// Every test the filter matched must have failed. A filter that also matched passing tests is too
		// broad to say which one reproduced the report, and one that matched somebody else's failing test
		// would confirm "red" on a failure the new test had nothing to do with.
		if (failed.Value != total.Value)
		{
			return new RedTestVerdict(
				false,
				$"Your filter matched {total.Value} tests and only {failed.Value} failed, so it also selects other tests. "
					+ "Narrow it to select only your new test.");
		}

		return new RedTestVerdict(true, "The test fails on the unfixed code.");
	}

	[GeneratedRegex(@"total:\s*(?<n>\d+)", RegexOptions.IgnoreCase)]
	private static partial Regex TotalPattern();

	[GeneratedRegex(@"failed:\s*(?<n>\d+)", RegexOptions.IgnoreCase)]
	private static partial Regex FailedPattern();

	private static int? ReadCount(Regex pattern, string output)
	{
		var matches = pattern.Matches(output);

		// The last one: a run prints its own summary at the end, after anything a test wrote.
		return matches.Count > 0
			&& int.TryParse(matches[^1].Groups["n"].Value, CultureInfo.InvariantCulture, out var n)
				? n
				: null;
	}
}
