namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// What one half of the check produced.
/// </summary>
/// <param name="Succeeded">Whether it passed.</param>
/// <param name="Output">What it said, which is the correction the model reads on failure.</param>
public sealed record IssueFixCheckStep(bool Succeeded, string Output);

/// <summary>
/// The model's per-attempt check for a fix that has no rule behind it.
/// </summary>
/// <remarks>
/// Every other AI fix here is checked by re-running the rule that asked for it: free, exact, and the
/// reason a 27b model can be pointed at a clone at all. An issue-driven fix has no rule, so the check
/// becomes build-then-test.
/// <para>
/// By the time this runs the reproducing test is already in the tree, so a green suite is one that
/// includes it, and the model is told so. This is feedback between attempts, not the decision to
/// deliver: the pipeline's own gates decide that, and re-check everything, including that the
/// reproducing test failed before the fix.
/// </para>
/// </remarks>
public sealed class IssueFixOracle(
	Func<CancellationToken, Task<IssueFixCheckStep>> buildAsync,
	Func<CancellationToken, Task<IssueFixCheckStep>> testAsync)
{
	/// <summary>
	/// How much build or test output the model is shown.
	/// </summary>
	/// <remarks>
	/// A full test log would fill the context window and bury the first error, which is the one that
	/// matters. The tail is kept rather than the head: compilers and test runners put their summary
	/// last.
	/// </remarks>
	public const int MaxOutputCharacters = 4_000;

	/// <summary>
	/// Runs the check as the clone now stands.
	/// </summary>
	/// <param name="cancellationToken">Signalled when the user stops the work item.</param>
	public async Task<AiRuleCheck> CheckAsync(CancellationToken cancellationToken)
	{
		var build = await buildAsync(cancellationToken).ConfigureAwait(false);

		// Tests are not run against code that does not compile. They would cost minutes and tell the
		// model nothing the compiler has not already said more precisely.
		if (!build.Succeeded)
		{
			return new AiRuleCheck(
				false,
				$"The change does not compile:\n{Tail(build.Output)}");
		}

		var test = await testAsync(cancellationToken).ConfigureAwait(false);

		return test.Succeeded
			? new AiRuleCheck(
				true,
				"It compiles and every test passes, including the reproducing test that failed before "
					+ "your change. Finish now.")
			: new AiRuleCheck(
				false,
				$"It compiles, but tests fail:\n{Tail(test.Output)}");
	}

	private static string Tail(string output)
		=> output.Length <= MaxOutputCharacters
			? output
			: "(earlier output omitted)\n" + output[^MaxOutputCharacters..];
}
