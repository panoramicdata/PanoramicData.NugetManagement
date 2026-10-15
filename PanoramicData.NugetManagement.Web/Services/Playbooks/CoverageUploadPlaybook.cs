using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Services.Playbooks;

/// <summary>
/// TST-10: raise line coverage by writing tests.
/// </summary>
/// <remarks>
/// This used to describe adding a CI coverage job, which was the wrong job for the model: that edit is
/// deterministic and belongs to CI-15's remediation, and a session asked to do it spent twelve turns
/// looking for a reference file it could not read. What only a model can do is write the tests, and
/// what it needs for that is an oracle that does not wait for a push — so the session is given
/// <c>run_coverage</c>, and is judged on the figure that tool reports.
/// <para>
/// A repository with no coverage figure at all is not a candidate (see <see cref="AiFixCandidates"/>):
/// writing tests cannot make Codacy receive a report.
/// </para>
/// </remarks>
public sealed class CoverageUploadPlaybook : IRuleAiPlaybook
{
	/// <inheritdoc />
	public string RuleId => "TST-10";

	/// <inheritdoc />
	public string Goal
		=> "Add unit tests so that line coverage reaches the target given below.";

	/// <inheritdoc />
	public IReadOnlyList<string> Files => ["test files inside the test project's folder"];

	/// <inheritdoc />
	public string ExpectedEndState
		=> "run_coverage reports line coverage at or above the target, and none of your tests fails. Tests it "
			+ "says failed before you started are not yours: ignore them. You wrote only inside the test "
			+ "project's folder, and only new files: existing files are read-only. You did not change the "
			+ "code being tested.";

	/// <inheritdoc />
	public string WorkedExample
		=> """
			How to work:
			1. Call run_coverage. It names the files with the most lines that never run, and which lines.
			2. Read the one file with the most uncovered lines. Look at one existing test for the test framework
			   and assertion library to use. But many of the existing tests are integration tests that call a
			   live service with credentials: they are what is failing now, and you must NOT copy them. Do not
			   use any TestConfiguration, appsettings, user secrets or environment variable, and do not touch
			   the network. Every test you write must pass on a machine with none of those.
			3. Write a new test file for that class. Each write tells you whether the test project still compiles:
			   fix any error it reports before writing anything else. One focused test per public method or branch. Call the
			   real code with simple inputs, and assert on the result.
			4. Call run_coverage again to see the figure move. Repeat for the next file.
			Prefer many small, plain tests over a few clever ones. If a test fails, fix the test, not the code.

			Do not copy and paste. Codacy grades duplicated code as poor, and a file of near-identical tests
			fails it. Where several tests differ only in their inputs, write one test with [Theory] and
			[InlineData] (or [MemberData]). Put setup that more than one test needs in a private helper
			method. Keep each test file short, and give every test class and method a clear name.

			For a class that calls HTTP, give it a client whose handler returns a fixed response:

			  private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
			  {
			      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			          => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
			  }

			For a model or options class, create it, set its properties, and assert they read back. If it
			serialises, round-trip it through System.Text.Json and compare.

			Put each new test class in its own file inside the test project folder, named after the class
			it tests, for example ProfilesTests.cs.
			""";
}
