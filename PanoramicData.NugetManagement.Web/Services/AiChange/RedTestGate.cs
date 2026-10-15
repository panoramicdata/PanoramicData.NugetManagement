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
public static class RedTestGate
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

		return new RedTestVerdict(true, "The test fails on the unfixed code.");
	}
}
