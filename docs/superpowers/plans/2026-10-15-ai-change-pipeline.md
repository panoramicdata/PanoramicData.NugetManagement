# AI Change Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route every AI change (rule fixes and issue fixes) through one pipeline that branches, proves the change with tests (red then green for bugs), opens a pull request, waits for CI, and merges it from this app.

**Architecture:** A new `AiChangePipeline` in `PanoramicData.NugetManagement.Web/Services/AiChange/` takes its collaborators as small ports (git, tests, the model-facing work, the GitHub write API) so every gate is unit-tested with hand-written fakes, as `AiFixSession` already is. Pure gate classes (`RedTestGate`, `ChangedLineCoverage`, `CheckRunGate`) hold the decisions. Thin adapters wrap `LocalRepoService` and `AiFixSession`. The two executors in `WorkExecutors.cs` call the pipeline instead of leaving a dirty clone.

**Tech Stack:** .NET 10, xUnit v3 (Microsoft.Testing.Platform), AwesomeAssertions, Octokit, no mocking library (hand-written fakes).

**Spec:** `docs/superpowers/specs/2026-10-15-ai-change-pipeline-design.md`

## Global Constraints

- Tabs, not spaces, in C#, XML, JSON and MSBuild files; follow `.editorconfig`; fix `IDE0055` in files you touch; do not reformat unrelated code.
- `TreatWarningsAsErrors` stays on. Never skip or delete a test to get green.
- Build only the affected project (`PanoramicData.NugetManagement.Test`), not the whole solution.
- Tests: xUnit v3 `[Fact]`/`[Theory]`, AwesomeAssertions `.Should()`, classes derive `TestWithOutput(output)`, use `TestContext.Current.CancellationToken`.
- Verify with the test exe directly: `./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*ClassName"`. Prefer `--filter-class` (reports `total: 0` when the class is missing) and confirm the count is what you expect.
- If the dev server holds the build lock, build with `-p:OutDir=<repo>\PanoramicData.NugetManagement.Test\bin\Verify\net10.0\` (it must be inside the repo tree).
- No em dashes in code comments, commit messages or docs.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Changed-line coverage threshold 0.60, squash merge, CI wait 30 minutes, three red-test attempts (spec decisions 1 to 6).
- Work in the worktree `.claude/worktrees/ai-change-pipeline` on branch `feat/ai-change-pipeline`; other sessions commit to `main`, so rebase before pushing.

## Review Focus

- CI check runs are not registered for a moment after a push: an empty list right away must not be read as "this repo has no CI" (a grace period before `NoChecks` is believed). Pinned in Task 4.
- A "red" test that fails because it does not compile, or that matched no tests, is not a reproduction. Pinned in Task 3.
- The unattended path cap (3 paths) would reject any fix that also adds a test file, so test files must not count against it. Pinned in Task 4.
- The PR head can move between the local gate and the merge (a push to the branch); the merge must be conditional on the SHA that was gated. Pinned in Task 4.
- A stop (cancellation) or a throw mid-pipeline must still return the clone to `main` and not leave a branch or dirty files. Pinned in Task 4.

---

### Task 1: Check-run gate and the pull request operations on the GitHub write API

**Files:**
- Create: `PanoramicData.NugetManagement/Services/CheckRunGate.cs`
- Modify: `PanoramicData.NugetManagement/Services/IGitHubWriteApi.cs`
- Modify: `PanoramicData.NugetManagement/Services/OctokitGitHubWriteApi.cs`
- Modify (fakes that implement the interface): `PanoramicData.NugetManagement.Test/DependabotTriageRunnerTests.cs`, `PanoramicData.NugetManagement.Test/UncoveredDependencyIssueServiceTests.cs`
- Test: `PanoramicData.NugetManagement.Test/CheckRunGateTests.cs`

**Interfaces:**
- Produces: `CheckRunConclusion { Pending, Passed, Failed }`, `CheckRunState(string Name, CheckRunConclusion Conclusion)`, `CheckGateResult { NoChecks, Pending, Passed, Failed }`, `CheckRunGate.Evaluate(IReadOnlyList<CheckRunState>)`.
- Produces on `IGitHubWriteApi`:
  - `Task<int> CreatePullRequestAsync(string owner, string name, string head, string baseBranch, string title, string body, CancellationToken cancellationToken)`
  - `Task<IReadOnlyList<CheckRunState>> GetCheckRunsAsync(string owner, string name, string sha, CancellationToken cancellationToken)`
  - `Task MergePullRequestAsync(string owner, string name, int number, string expectedHeadSha, CancellationToken cancellationToken)`

- [ ] **Step 1: Write the failing test**

```csharp
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="CheckRunGate"/>: what a pull request's check runs add up to.
/// </summary>
public class CheckRunGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static CheckRunState Run(CheckRunConclusion conclusion) => new("build", conclusion);

	[Fact]
	public void Evaluate_NoRuns_IsNoChecks()
		=> CheckRunGate.Evaluate([]).Should().Be(CheckGateResult.NoChecks);

	[Fact]
	public void Evaluate_AllPassed_IsPassed()
		=> CheckRunGate.Evaluate([Run(CheckRunConclusion.Passed), Run(CheckRunConclusion.Passed)])
			.Should().Be(CheckGateResult.Passed);

	[Fact]
	public void Evaluate_SomethingStillRunning_IsPending()
		=> CheckRunGate.Evaluate([Run(CheckRunConclusion.Passed), Run(CheckRunConclusion.Pending)])
			.Should().Be(CheckGateResult.Pending);

	[Fact]
	public void Evaluate_AFailureWinsOverPending()
		=> CheckRunGate.Evaluate([Run(CheckRunConclusion.Pending), Run(CheckRunConclusion.Failed)])
			.Should().Be(CheckGateResult.Failed,
				"a failed check cannot be undone by a slower one finishing, so there is no reason to keep waiting");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`
Expected: FAIL to compile, `CheckRunGate` does not exist.

- [ ] **Step 3: Write the minimal implementation**

`PanoramicData.NugetManagement/Services/CheckRunGate.cs`:

```csharp
namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// How one check run on a pull request's head commit stands.
/// </summary>
public enum CheckRunConclusion
{
	/// <summary>Queued or still running.</summary>
	Pending,

	/// <summary>Finished successfully, was neutral, or was skipped.</summary>
	Passed,

	/// <summary>Finished unsuccessfully, was cancelled, or timed out.</summary>
	Failed
}

/// <summary>
/// One check run.
/// </summary>
/// <param name="Name">The check's name, for saying which one failed.</param>
/// <param name="Conclusion">Where it stands.</param>
public sealed record CheckRunState(string Name, CheckRunConclusion Conclusion);

/// <summary>
/// What a pull request's check runs add up to.
/// </summary>
public enum CheckGateResult
{
	/// <summary>No check runs exist (yet).</summary>
	NoChecks,

	/// <summary>None has failed and at least one has not finished.</summary>
	Pending,

	/// <summary>Every check finished and none failed.</summary>
	Passed,

	/// <summary>At least one check failed.</summary>
	Failed
}

/// <summary>
/// Reduces check runs to the one answer a merge decision needs.
/// </summary>
public static class CheckRunGate
{
	/// <summary>
	/// Adds up a set of check runs.
	/// </summary>
	/// <param name="runs">The check runs on the pull request's head commit.</param>
	/// <returns>The combined result; a failure wins over pending.</returns>
	public static CheckGateResult Evaluate(IReadOnlyList<CheckRunState> runs)
	{
		if (runs.Count == 0)
		{
			return CheckGateResult.NoChecks;
		}

		if (runs.Any(run => run.Conclusion == CheckRunConclusion.Failed))
		{
			return CheckGateResult.Failed;
		}

		return runs.Any(run => run.Conclusion == CheckRunConclusion.Pending)
			? CheckGateResult.Pending
			: CheckGateResult.Passed;
	}
}
```

Add to `IGitHubWriteApi` (before the closing brace):

```csharp
	/// <summary>
	/// Opens a pull request.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="head">The branch holding the change.</param>
	/// <param name="baseBranch">The branch it is to merge into.</param>
	/// <param name="title">The pull request title.</param>
	/// <param name="body">The pull request body, in markdown.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The new pull request's number.</returns>
	Task<int> CreatePullRequestAsync(
		string owner,
		string name,
		string head,
		string baseBranch,
		string title,
		string body,
		CancellationToken cancellationToken);

	/// <summary>
	/// Reads the check runs on a commit.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="sha">The commit, normally a pull request's head.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<IReadOnlyList<CheckRunState>> GetCheckRunsAsync(
		string owner,
		string name,
		string sha,
		CancellationToken cancellationToken);

	/// <summary>
	/// Squash-merges a pull request, but only if its head is still the commit that was checked.
	/// </summary>
	/// <param name="owner">The repository owner.</param>
	/// <param name="name">The repository name.</param>
	/// <param name="number">The pull request number.</param>
	/// <param name="expectedHeadSha">The head commit the checks were run against.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <remarks>
	/// Throws when GitHub refuses, for instance because the head has moved since it was checked. The
	/// caller treats that as a refusal to merge, not as something to retry blindly.
	/// </remarks>
	Task MergePullRequestAsync(
		string owner,
		string name,
		int number,
		string expectedHeadSha,
		CancellationToken cancellationToken);
```

Add to `OctokitGitHubWriteApi` (before the closing brace):

```csharp
	/// <inheritdoc />
	public async Task<int> CreatePullRequestAsync(
		string owner,
		string name,
		string head,
		string baseBranch,
		string title,
		string body,
		CancellationToken cancellationToken)
	{
		var created = await _github.PullRequest
			.Create(owner, name, new NewPullRequest(title, head, baseBranch) { Body = body })
			.ConfigureAwait(false);

		return created.Number;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<CheckRunState>> GetCheckRunsAsync(
		string owner,
		string name,
		string sha,
		CancellationToken cancellationToken)
	{
		var response = await _github.Check.Run.GetAllForReference(owner, name, sha).ConfigureAwait(false);

		return [.. response.CheckRuns.Select(run => new CheckRunState(run.Name, Classify(run)))];
	}

	/// <inheritdoc />
	/// <remarks>
	/// The expected SHA is sent so GitHub itself refuses a merge whose head has moved, rather than this
	/// code checking and then merging a different commit in the gap between.
	/// </remarks>
	public async Task MergePullRequestAsync(
		string owner,
		string name,
		int number,
		string expectedHeadSha,
		CancellationToken cancellationToken)
		=> await _github.PullRequest
			.Merge(owner, name, number, new MergePullRequest
			{
				Sha = expectedHeadSha,
				MergeMethod = PullRequestMergeMethod.Squash
			})
			.ConfigureAwait(false);

	private static CheckRunConclusion Classify(CheckRun run)
	{
		if (run.Status != CheckStatus.Completed)
		{
			return CheckRunConclusion.Pending;
		}

		return run.Conclusion?.Value is CheckConclusion.Success or CheckConclusion.Neutral or CheckConclusion.Skipped
			? CheckRunConclusion.Passed
			: CheckRunConclusion.Failed;
	}
```

Then give each existing fake that implements `IGitHubWriteApi` (find them with `grep -n "IGitHubWriteApi" PanoramicData.NugetManagement.Test/*.cs`) the three new members, each throwing `NotSupportedException` since those tests never open pull requests:

```csharp
	public Task<int> CreatePullRequestAsync(string owner, string name, string head, string baseBranch, string title, string body, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task<IReadOnlyList<CheckRunState>> GetCheckRunsAsync(string owner, string name, string sha, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task MergePullRequestAsync(string owner, string name, int number, string expectedHeadSha, CancellationToken cancellationToken)
		=> throw new NotSupportedException();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj` then `./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-class "*CheckRunGateTests"`
Expected: build succeeds with 0 warnings; `total: 4, succeeded: 4`. Also run `--filter-class "*DependabotTriageRunnerTests"` and `--filter-class "*UncoveredDependencyIssueServiceTests"` and `--filter-class "*CoverageOptOutTests"` to confirm nothing else broke.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement/Services PanoramicData.NugetManagement.Test
git commit -m "feat: add pull request create, check-run read and conditional merge to the GitHub write API"
```

---

### Task 2: Coverage of changed lines

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/ChangedLineCoverage.cs`
- Test: `PanoramicData.NugetManagement.Test/ChangedLineCoverageTests.cs`

**Interfaces:**
- Produces: `ChangedFile(string Path, IReadOnlySet<int> Lines)`, `ChangedLineCoverageResult(bool Passed, int Executable, int Covered, string Message)`, `ChangedLineCoverage.Threshold` (0.60), `ChangedLineCoverage.Evaluate(IReadOnlyList<ChangedFile> changed, string coberturaXml)`.

- [ ] **Step 1: Write the failing test**

```csharp
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ChangedLineCoverage"/>: whether the lines a change touched are exercised.
/// </summary>
public class ChangedLineCoverageTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string Cobertura = """
		<coverage>
		  <packages><package><classes>
		    <class filename="C:\src\repo\Lib\Parser.cs">
		      <lines>
		        <line number="10" hits="4" />
		        <line number="11" hits="0" />
		        <line number="12" hits="2" />
		      </lines>
		    </class>
		  </classes></package></packages>
		</coverage>
		""";

	private static ChangedFile Changed(string path, params int[] lines) => new(path, lines.ToHashSet());

	[Fact]
	public void Evaluate_PassesWhenEnoughChangedExecutableLinesRan()
	{
		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Parser.cs", 10, 12)], Cobertura);

		result.Passed.Should().BeTrue();
		result.Covered.Should().Be(2);
		result.Executable.Should().Be(2);
	}

	[Fact]
	public void Evaluate_FailsBelowTheThreshold()
	{
		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Parser.cs", 10, 11)], Cobertura);

		result.Passed.Should().BeFalse("one of two executable changed lines is 50%, under the 60% bar");
		result.Message.Should().Contain("Lib/Parser.cs");
	}

	[Fact]
	public void Evaluate_IgnoresChangedLinesThatAreNotExecutable()
	{
		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Parser.cs", 1, 2, 12)], Cobertura);

		result.Executable.Should().Be(1, "lines 1 and 2 are comments or usings and have no coverage entry");
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public void Evaluate_PassesWhenNoChangedLineIsExecutable()
	{
		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Parser.cs", 1)], Cobertura);

		result.Passed.Should().BeTrue("a change with nothing to execute has nothing to leave uncovered");
		result.Executable.Should().Be(0);
	}

	[Fact]
	public void Evaluate_TreatsAChangedFileMissingFromTheReportAsUncovered()
	{
		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Other.cs", 5)], Cobertura);

		result.Passed.Should().BeFalse(
			"a changed code file the coverage run never saw has not been exercised, and passing it would "
				+ "let any unreferenced file through");
	}
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`
Expected: FAIL to compile, `ChangedLineCoverage` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Xml.Linq;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// A file a change touched and the (new-side) line numbers it added or modified.
/// </summary>
/// <param name="Path">The repository-relative path, with forward slashes.</param>
/// <param name="Lines">The changed line numbers.</param>
public sealed record ChangedFile(string Path, IReadOnlySet<int> Lines);

/// <summary>
/// How well the changed lines were exercised.
/// </summary>
/// <param name="Passed">Whether the threshold was met.</param>
/// <param name="Executable">How many changed lines are executable.</param>
/// <param name="Covered">How many of those ran.</param>
/// <param name="Message">What to tell the model or the user.</param>
public sealed record ChangedLineCoverageResult(bool Passed, int Executable, int Covered, string Message);

/// <summary>
/// Whether the lines a change touched are covered by the tests that ran.
/// </summary>
/// <remarks>
/// The bar is the one the TST rules already use (60% line coverage is green), applied to the lines this
/// change touched rather than to the repository. A changed file the report never mentions counts as
/// fully uncovered: otherwise a new file that nothing references would pass by being invisible.
/// </remarks>
public static class ChangedLineCoverage
{
	/// <summary>The fraction of changed executable lines that must have run.</summary>
	public const double Threshold = 0.60;

	/// <summary>
	/// Checks the changed lines against a Cobertura report.
	/// </summary>
	/// <param name="changed">The changed files and lines.</param>
	/// <param name="coberturaXml">The report from the test run that included the change.</param>
	public static ChangedLineCoverageResult Evaluate(IReadOnlyList<ChangedFile> changed, string coberturaXml)
	{
		var hitsByFile = ReadHits(coberturaXml);
		var executable = 0;
		var covered = 0;
		var worst = new List<string>();

		foreach (var file in changed)
		{
			var hits = FindHits(hitsByFile, file.Path);

			if (hits is null)
			{
				executable += file.Lines.Count;
				worst.Add($"{file.Path} is not in the coverage report");
				continue;
			}

			var fileExecutable = file.Lines.Where(hits.ContainsKey).ToList();
			var fileCovered = fileExecutable.Count(line => hits[line] > 0);

			executable += fileExecutable.Count;
			covered += fileCovered;

			if (fileExecutable.Count > 0 && (double)fileCovered / fileExecutable.Count < Threshold)
			{
				worst.Add($"{file.Path}: {fileCovered} of {fileExecutable.Count} changed lines ran");
			}
		}

		var passed = executable == 0 || (double)covered / executable >= Threshold;

		var message = passed
			? $"{covered} of {executable} changed executable lines are covered."
			: $"Only {covered} of {executable} changed executable lines are covered, below {Threshold:P0}. "
				+ string.Join("; ", worst) + ". Add tests that execute the changed code.";

		return new ChangedLineCoverageResult(passed, executable, covered, message);
	}

	private static Dictionary<string, Dictionary<int, int>> ReadHits(string coberturaXml)
	{
		var result = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);

		foreach (var cls in XDocument.Parse(coberturaXml).Descendants("class"))
		{
			var filename = ((string?)cls.Attribute("filename") ?? string.Empty).Replace('\\', '/');

			if (!result.TryGetValue(filename, out var lines))
			{
				lines = [];
				result[filename] = lines;
			}

			// Only the class's own <lines>: each method repeats its lines inside, and counting both
			// would double every executable line.
			foreach (var line in cls.Elements("lines").Elements("line"))
			{
				var number = (int?)line.Attribute("number");
				var hits = (int?)line.Attribute("hits") ?? 0;

				if (number is not null)
				{
					// A partial class appears once per part, so the most hits seen wins.
					lines[number.Value] = Math.Max(lines.GetValueOrDefault(number.Value), hits);
				}
			}
		}

		return result;
	}

	private static Dictionary<int, int>? FindHits(Dictionary<string, Dictionary<int, int>> byFile, string path)
	{
		var wanted = path.Replace('\\', '/');

		foreach (var (filename, hits) in byFile)
		{
			if (filename.Equals(wanted, StringComparison.OrdinalIgnoreCase)
				|| filename.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase))
			{
				return hits;
			}
		}

		return null;
	}
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: build, then `--filter-class "*ChangedLineCoverageTests"`
Expected: `total: 5, succeeded: 5`.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/AiChange PanoramicData.NugetManagement.Test/ChangedLineCoverageTests.cs
git commit -m "feat: measure coverage of the lines a change touched"
```

---

### Task 3: The red-test gate

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/RedTestGate.cs`
- Test: `PanoramicData.NugetManagement.Test/RedTestGateTests.cs`

**Interfaces:**
- Produces: `TestRun(bool Passed, string Output)`, `RedTestVerdict(bool IsValidRed, string Message)`, `RedTestGate.Validate(TestRun unfixed)`.

- [ ] **Step 1: Write the failing test**

```csharp
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="RedTestGate"/>: whether a failing run is a reproduction or just a failure.
/// </summary>
public class RedTestGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void Validate_AFailingAssertionIsAValidRed()
	{
		var verdict = RedTestGate.Validate(new TestRun(false, "Expected result to be 3, but found 2.\n  total: 1\n  failed: 1"));

		verdict.IsValidRed.Should().BeTrue();
	}

	[Fact]
	public void Validate_APassingTestIsNotRed()
	{
		var verdict = RedTestGate.Validate(new TestRun(true, "total: 1\n  failed: 0\n  succeeded: 1"));

		verdict.IsValidRed.Should().BeFalse();
		verdict.Message.Should().Contain("passes", "the model needs to know it reproduced nothing");
	}

	[Fact]
	public void Validate_ACompileErrorIsNotRed()
	{
		var verdict = RedTestGate.Validate(new TestRun(false, "Parser.cs(4,9): error CS0103: The name 'Foo' does not exist\nBuild FAILED."));

		verdict.IsValidRed.Should().BeFalse("a test that does not compile fails for every bug and none");
		verdict.Message.Should().Contain("compile");
	}

	[Fact]
	public void Validate_ARunThatMatchedNoTestsIsNotRed()
	{
		var verdict = RedTestGate.Validate(new TestRun(false, "Zero tests ran\n  total: 0"));

		verdict.IsValidRed.Should().BeFalse("nothing ran, so nothing was reproduced");
		verdict.Message.Should().Contain("no tests");
	}
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: build. Expected: FAIL to compile, `RedTestGate` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: build, then `--filter-class "*RedTestGateTests"`
Expected: `total: 4, succeeded: 4`.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/AiChange/RedTestGate.cs PanoramicData.NugetManagement.Test/RedTestGateTests.cs
git commit -m "feat: decide whether a failing test run is a reproduction"
```

---

### Task 4: The pipeline and its ports

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/AiChangePorts.cs`
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/AiChangePipeline.cs`
- Test: `PanoramicData.NugetManagement.Test/AiChangePipelineFakes.cs`
- Test: `PanoramicData.NugetManagement.Test/AiChangePipelineTests.cs`

**Interfaces:**
- Consumes: `ChangedFile`, `ChangedLineCoverage`, `RedTestGate`, `TestRun` (Tasks 2, 3); `CheckRunGate`, `CheckRunState`, `IGitHubWriteApi` additions (Task 1); `AiFixOutcome` and `IssueFixCheckStep` (existing).
- Produces:
  - `AiChangeKind { RuleFix, IssueFix }`
  - `AiChangeRequest(string Owner, string Name, AiChangeKind Kind, string Slug, string Title, int? IssueNumber, bool RequiresRedTest)`
  - `AiChangeOutcomeKind { Merged, NoValidRedTest, LocalGateFailed, LeftOpenCiFailed, LeftOpenCiTimedOut, LeftOpenMergeRefused, Refused }`
  - `AiChangeOutcome(AiChangeOutcomeKind Kind, string Message, int? PullRequestNumber = null)`
  - `AiChangeLimits(Func<string, bool> IsAllowedPath, Func<string, bool> IsTestPath, int MaxPaths)`
  - `AiChangeOptions` with `Limits`, `MaxRedAttempts` (3), `CiTimeout` (30 min), `PollInterval` (30 s), `NoChecksGrace` (2 min), `Delay` (`Func<TimeSpan, CancellationToken, Task>`)
  - ports `IAiChangeGit`, `IAiChangeTests`, `IAiChangeWork`
  - `AiChangePipeline.RunAsync(AiChangeRequest request, Action<string> say, CancellationToken cancellationToken)`

- [ ] **Step 1: Write the ports**

`AiChangePorts.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>What kind of AI task is asking for a change.</summary>
public enum AiChangeKind
{
	/// <summary>A governance rule that has no deterministic remediation.</summary>
	RuleFix,

	/// <summary>A human-raised issue.</summary>
	IssueFix
}

/// <summary>
/// One change the AI is to make and deliver.
/// </summary>
/// <param name="Owner">The repository owner.</param>
/// <param name="Name">The repository name.</param>
/// <param name="Kind">What is asking.</param>
/// <param name="Slug">A unique, branch-safe label such as <c>issue-42-a1b2</c>; the branch is <c>ai/&lt;slug&gt;</c>.</param>
/// <param name="Title">The pull request title.</param>
/// <param name="IssueNumber">The issue, when there is one; it is closed by the pull request.</param>
/// <param name="RequiresRedTest">True for a reported bug, which must be reproduced by a failing test first.</param>
public sealed record AiChangeRequest(
	string Owner,
	string Name,
	AiChangeKind Kind,
	string Slug,
	string Title,
	int? IssueNumber,
	bool RequiresRedTest);

/// <summary>How an attempt ended.</summary>
public enum AiChangeOutcomeKind
{
	/// <summary>The pull request was merged.</summary>
	Merged,

	/// <summary>No test could be made to reproduce the report; the issue was told.</summary>
	NoValidRedTest,

	/// <summary>A local gate failed; nothing was pushed.</summary>
	LocalGateFailed,

	/// <summary>A CI check failed; the pull request is left open.</summary>
	LeftOpenCiFailed,

	/// <summary>CI did not finish in time; the pull request is left open.</summary>
	LeftOpenCiTimedOut,

	/// <summary>GitHub refused the merge (for instance the head moved); the pull request is left open.</summary>
	LeftOpenMergeRefused,

	/// <summary>The clone was not in a state to start from.</summary>
	Refused
}

/// <summary>
/// The result of an attempt.
/// </summary>
/// <param name="Kind">How it ended.</param>
/// <param name="Message">One line saying why, for the console.</param>
/// <param name="PullRequestNumber">The pull request, when one was opened.</param>
public sealed record AiChangeOutcome(AiChangeOutcomeKind Kind, string Message, int? PullRequestNumber = null);

/// <summary>
/// What the AI may touch.
/// </summary>
/// <param name="IsAllowedPath">Whether a repository-relative path may be written.</param>
/// <param name="IsTestPath">Whether a path is a test file; these do not count against the cap.</param>
/// <param name="MaxPaths">The most non-test paths one change may touch.</param>
public sealed record AiChangeLimits(Func<string, bool> IsAllowedPath, Func<string, bool> IsTestPath, int MaxPaths);

/// <summary>
/// The pipeline's tunables.
/// </summary>
public sealed class AiChangeOptions
{
	/// <summary>What the AI may touch.</summary>
	public required AiChangeLimits Limits { get; init; }

	/// <summary>How many tries the model gets at a valid red test.</summary>
	public int MaxRedAttempts { get; init; } = 3;

	/// <summary>How long to wait for CI before leaving the pull request open.</summary>
	public TimeSpan CiTimeout { get; init; } = TimeSpan.FromMinutes(30);

	/// <summary>How often to ask GitHub for the checks.</summary>
	public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// How long an empty list of checks is believed to be transient. GitHub registers check runs a moment
	/// after a push, so "no checks" straight away does not mean "no CI".
	/// </summary>
	public TimeSpan NoChecksGrace { get; init; } = TimeSpan.FromMinutes(2);

	/// <summary>Waits between polls; replaced in tests so they do not sleep.</summary>
	public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>The git operations the pipeline needs, on one clone.</summary>
public interface IAiChangeGit
{
	/// <summary>
	/// Fast-forwards <c>main</c> and confirms the clone is clean and on it.
	/// </summary>
	/// <returns>Null when ready; otherwise why the clone cannot be started from.</returns>
	Task<string?> RefusalAsync(CancellationToken cancellationToken);

	/// <summary>Creates and checks out a branch from <c>main</c>.</summary>
	Task CreateBranchAsync(string branch, CancellationToken cancellationToken);

	/// <summary>The files and lines changed relative to <c>main</c>, committed or not.</summary>
	Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(CancellationToken cancellationToken);

	/// <summary>Stages everything and commits it.</summary>
	/// <returns>The new commit's SHA.</returns>
	Task<string> CommitAsync(string message, CancellationToken cancellationToken);

	/// <summary>Pushes the branch to origin.</summary>
	Task PushBranchAsync(string branch, CancellationToken cancellationToken);

	/// <summary>
	/// Returns the clone to <c>main</c>: discards anything uncommitted, deletes the local branch,
	/// optionally deletes the remote one, and fast-forwards <c>main</c>.
	/// </summary>
	Task ReturnToMainAsync(string branch, bool deleteRemoteBranch, CancellationToken cancellationToken);
}

/// <summary>Building and testing the clone.</summary>
public interface IAiChangeTests
{
	/// <summary>Builds the clone.</summary>
	Task<IssueFixCheckStep> BuildAsync(CancellationToken cancellationToken);

	/// <summary>Runs the whole suite.</summary>
	Task<TestRun> RunAllAsync(CancellationToken cancellationToken);

	/// <summary>Runs only the tests a filter selects.</summary>
	Task<TestRun> RunOnlyAsync(string testFilter, CancellationToken cancellationToken);

	/// <summary>Runs the suite with coverage.</summary>
	/// <returns>The Cobertura report, or null if none could be produced.</returns>
	Task<string?> CoberturaAsync(CancellationToken cancellationToken);
}

/// <summary>The model-facing work: writing the reproducing test and making the fix.</summary>
public interface IAiChangeWork
{
	/// <summary>
	/// Has the model write a test that reproduces the report.
	/// </summary>
	/// <param name="request">What is being fixed.</param>
	/// <param name="feedback">Why the previous attempt was rejected, or null on the first.</param>
	/// <param name="cancellationToken">Signalled when the user stops the work.</param>
	/// <returns>A test filter that selects the new test, or null if the model gave up.</returns>
	Task<string?> WriteRedTestAsync(AiChangeRequest request, string? feedback, CancellationToken cancellationToken);

	/// <summary>
	/// Has the model change the code.
	/// </summary>
	/// <param name="request">What is being fixed.</param>
	/// <param name="redTestFilter">The reproducing test it must turn green, or null for a rule fix.</param>
	/// <param name="cancellationToken">Signalled when the user stops the work.</param>
	Task<AiFixOutcome> FixAsync(AiChangeRequest request, string? redTestFilter, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the fakes**

`PanoramicData.NugetManagement.Test/AiChangePipelineFakes.cs`:

```csharp
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

internal sealed class FakeAiChangeGit : IAiChangeGit
{
	public string? Refusal { get; set; }
	public IReadOnlyList<ChangedFile> Changed { get; set; } = [new ChangedFile("Lib/Parser.cs", new HashSet<int> { 10 })];
	public List<string> Calls { get; } = [];
	public bool? DeletedRemote { get; private set; }

	public Task<string?> RefusalAsync(CancellationToken cancellationToken)
	{
		Calls.Add("refusal");
		return Task.FromResult(Refusal);
	}

	public Task CreateBranchAsync(string branch, CancellationToken cancellationToken)
	{
		Calls.Add($"branch:{branch}");
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(CancellationToken cancellationToken)
		=> Task.FromResult(Changed);

	public Task<string> CommitAsync(string message, CancellationToken cancellationToken)
	{
		Calls.Add("commit");
		return Task.FromResult("abc123");
	}

	public Task PushBranchAsync(string branch, CancellationToken cancellationToken)
	{
		Calls.Add("push");
		return Task.CompletedTask;
	}

	public Task ReturnToMainAsync(string branch, bool deleteRemoteBranch, CancellationToken cancellationToken)
	{
		Calls.Add("return");
		DeletedRemote = deleteRemoteBranch;
		return Task.CompletedTask;
	}
}

internal sealed class FakeAiChangeTests : IAiChangeTests
{
	public IssueFixCheckStep Build { get; set; } = new(true, string.Empty);
	public TestRun All { get; set; } = new(true, "total: 10\n  failed: 0");
	public Queue<TestRun> OnlyRuns { get; } = new();
	public string? Cobertura { get; set; } = """
		<coverage><packages><package><classes>
		  <class filename="C:\r\Lib\Parser.cs"><lines><line number="10" hits="3" /></lines></class>
		</classes></package></packages></coverage>
		""";

	public Task<IssueFixCheckStep> BuildAsync(CancellationToken cancellationToken) => Task.FromResult(Build);

	public Task<TestRun> RunAllAsync(CancellationToken cancellationToken) => Task.FromResult(All);

	public Task<TestRun> RunOnlyAsync(string testFilter, CancellationToken cancellationToken)
		=> Task.FromResult(OnlyRuns.Count > 0 ? OnlyRuns.Dequeue() : new TestRun(true, "total: 1"));

	public Task<string?> CoberturaAsync(CancellationToken cancellationToken) => Task.FromResult(Cobertura);
}

internal sealed class FakeAiChangeWork : IAiChangeWork
{
	public int RedAttempts { get; private set; }
	public List<string?> Feedback { get; } = [];
	public string? RedFilter { get; set; } = "FullyQualifiedName~ParserTests";
	public bool FixSucceeds { get; set; } = true;

	public Task<string?> WriteRedTestAsync(AiChangeRequest request, string? feedback, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		RedAttempts++;
		Feedback.Add(feedback);
		return Task.FromResult(RedFilter);
	}

	public Task<AiFixOutcome> FixAsync(AiChangeRequest request, string? redTestFilter, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(new AiFixOutcome(FixSucceeds, 1, "done"));
	}
}

internal sealed class FakeGitHubWriteApi : IGitHubWriteApi
{
	public Queue<IReadOnlyList<CheckRunState>> Checks { get; } = new();
	public IReadOnlyList<CheckRunState> WhenQueueEmpty { get; set; } = [new CheckRunState("ci", CheckRunConclusion.Passed)];
	public List<string> Calls { get; } = [];
	public List<string> Comments { get; } = [];
	public string? MergedSha { get; private set; }
	public bool MergeThrows { get; set; }

	public Task<int> CreatePullRequestAsync(string owner, string name, string head, string baseBranch, string title, string body, CancellationToken cancellationToken)
	{
		Calls.Add($"pr:{head}->{baseBranch}");
		return Task.FromResult(7);
	}

	public Task<IReadOnlyList<CheckRunState>> GetCheckRunsAsync(string owner, string name, string sha, CancellationToken cancellationToken)
	{
		Calls.Add("checks");
		return Task.FromResult(Checks.Count > 0 ? Checks.Dequeue() : WhenQueueEmpty);
	}

	public Task MergePullRequestAsync(string owner, string name, int number, string expectedHeadSha, CancellationToken cancellationToken)
	{
		Calls.Add("merge");

		if (MergeThrows)
		{
			throw new InvalidOperationException("Head branch was modified");
		}

		MergedSha = expectedHeadSha;
		return Task.CompletedTask;
	}

	public Task CommentAsync(string owner, string name, int number, string body, CancellationToken cancellationToken)
	{
		Comments.Add(body);
		return Task.CompletedTask;
	}

	public Task<int> CreateIssueAsync(string owner, string name, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task UpdateIssueBodyAsync(string owner, string name, int number, string body, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task CloseIssueAsync(string owner, string name, int number, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task ClosePullRequestAsync(string owner, string name, int number, CancellationToken cancellationToken)
		=> throw new NotSupportedException();
}
```

- [ ] **Step 3: Write the failing tests**

`AiChangePipelineTests.cs`:

```csharp
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiChangePipeline"/>: every gate, and that the clone is always put back.
/// </summary>
public class AiChangePipelineTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private readonly FakeAiChangeGit _git = new();
	private readonly FakeAiChangeTests _tests = new();
	private readonly FakeAiChangeWork _work = new();
	private readonly FakeGitHubWriteApi _github = new();
	private readonly List<string> _said = [];
	private readonly List<TimeSpan> _waits = [];

	private static AiChangeRequest Issue() => new("o", "r", AiChangeKind.IssueFix, "issue-42-a1b2", "Fix #42", 42, true);

	private static AiChangeRequest Rule() => new("o", "r", AiChangeKind.RuleFix, "rule-ci-15-a1b2", "Fix CI-15", null, false);

	private AiChangePipeline Pipeline(Func<string, bool>? isAllowed = null, int maxPaths = 3)
		=> new(_git, _tests, _work, _github, new AiChangeOptions
		{
			Limits = new AiChangeLimits(
				isAllowed ?? (_ => true),
				path => path.Contains(".Test", StringComparison.Ordinal),
				maxPaths),
			Delay = (span, _) =>
			{
				_waits.Add(span);
				return Task.CompletedTask;
			}
		});

	private Task<AiChangeOutcome> RunAsync(AiChangeRequest request, CancellationToken? token = null)
		=> Pipeline().RunAsync(request, _said.Add, token ?? TestContext.Current.CancellationToken);

	private void RedThenGreen()
	{
		_tests.OnlyRuns.Enqueue(new TestRun(false, "Expected 3 but found 2.\n  total: 1\n  failed: 1"));
		_tests.OnlyRuns.Enqueue(new TestRun(true, "total: 1\n  failed: 0"));
	}

	[Fact]
	public async Task Run_AnIssueWithRedThenGreenAndGreenCi_IsMergedAtTheGatedSha()
	{
		RedThenGreen();

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged);
		outcome.PullRequestNumber.Should().Be(7);
		_github.MergedSha.Should().Be("abc123", "the merge is conditional on the commit that passed the gates");
		_git.DeletedRemote.Should().BeTrue("a merged branch is tidied away");
	}

	[Fact]
	public async Task Run_ARefusedCloneNeverBranches()
	{
		_git.Refusal = "has uncommitted changes";

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Refused);
		_git.Calls.Should().NotContain(call => call.StartsWith("branch:", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Run_NoValidRedTestInThreeTries_CommentsOnTheIssueAndStops()
	{
		_tests.OnlyRuns.Enqueue(new TestRun(true, "total: 1"));
		_tests.OnlyRuns.Enqueue(new TestRun(true, "total: 1"));
		_tests.OnlyRuns.Enqueue(new TestRun(true, "total: 1"));

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.NoValidRedTest);
		_work.RedAttempts.Should().Be(3);
		_work.Feedback[1].Should().Contain("passes", "the second try is told why the first was rejected");
		_github.Comments.Should().ContainSingle().Which.Should().Contain("test");
		_git.Calls.Should().NotContain("push");
		_git.Calls.Should().Contain("return");
	}

	[Fact]
	public async Task Run_ARuleFixSkipsTheRedStep()
	{
		var outcome = await RunAsync(Rule());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged);
		_work.RedAttempts.Should().Be(0);
	}

	[Fact]
	public async Task Run_AFixThatFails_PushesNothingAndReturnsToMain()
	{
		RedThenGreen();
		_work.FixSucceeds = false;

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LocalGateFailed);
		_git.Calls.Should().NotContain("push");
		_git.Calls.Should().Contain("return");
	}

	[Fact]
	public async Task Run_ARegressionInTheSuite_FailsTheLocalGate()
	{
		RedThenGreen();
		_tests.All = new TestRun(false, "total: 10\n  failed: 1");

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LocalGateFailed);
		outcome.Message.Should().Contain("tests");
	}

	[Fact]
	public async Task Run_UncoveredChangedCode_FailsTheLocalGate()
	{
		RedThenGreen();
		_tests.Cobertura = """
			<coverage><packages><package><classes>
			  <class filename="C:\r\Lib\Parser.cs"><lines><line number="10" hits="0" /></lines></class>
			</classes></package></packages></coverage>
			""";

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LocalGateFailed);
		outcome.Message.Should().Contain("covered");
	}

	[Fact]
	public async Task Run_ADisallowedPath_FailsTheLocalGate()
	{
		RedThenGreen();
		_git.Changed = [new ChangedFile("secrets/.env", new HashSet<int> { 1 })];

		var outcome = await Pipeline(isAllowed: path => !path.StartsWith("secrets/", StringComparison.Ordinal))
			.RunAsync(Issue(), _said.Add, TestContext.Current.CancellationToken);

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LocalGateFailed);
		outcome.Message.Should().Contain("secrets/.env");
	}

	[Fact]
	public async Task Run_TestFilesDoNotCountAgainstThePathCap()
	{
		RedThenGreen();
		_git.Changed =
		[
			new ChangedFile("Lib/A.cs", new HashSet<int> { 10 }),
			new ChangedFile("Lib/B.cs", new HashSet<int> { 10 }),
			new ChangedFile("Lib.Test/ATests.cs", new HashSet<int> { 1 }),
			new ChangedFile("Lib.Test/BTests.cs", new HashSet<int> { 1 })
		];
		_tests.Cobertura = """
			<coverage><packages><package><classes>
			  <class filename="C:\r\Lib\A.cs"><lines><line number="10" hits="1" /></lines></class>
			  <class filename="C:\r\Lib\B.cs"><lines><line number="10" hits="1" /></lines></class>
			  <class filename="C:\r\Lib.Test\ATests.cs"><lines><line number="1" hits="1" /></lines></class>
			  <class filename="C:\r\Lib.Test\BTests.cs"><lines><line number="1" hits="1" /></lines></class>
			</classes></package></packages></coverage>
			""";

		var outcome = await Pipeline(maxPaths: 2).RunAsync(Issue(), _said.Add, TestContext.Current.CancellationToken);

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged,
			"two production files and two test files is within a cap of two, because the tests that prove a fix are not part of its footprint");
	}

	[Fact]
	public async Task Run_AFailedCheck_LeavesThePullRequestOpenAndKeepsTheRemoteBranch()
	{
		RedThenGreen();
		_github.Checks.Enqueue([new CheckRunState("build", CheckRunConclusion.Failed)]);

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LeftOpenCiFailed);
		outcome.Message.Should().Contain("build");
		_github.Calls.Should().NotContain("merge");
		_git.DeletedRemote.Should().BeFalse("the pull request still needs its branch");
		_git.Calls.Should().Contain("return", "the clone is still put back on main");
	}

	[Fact]
	public async Task Run_ChecksThatNeverFinish_TimeOutAndLeaveThePullRequestOpen()
	{
		RedThenGreen();
		_github.WhenQueueEmpty = [new CheckRunState("ci", CheckRunConclusion.Pending)];

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LeftOpenCiTimedOut);
		_github.Calls.Should().NotContain("merge");
		_waits.Sum(span => span.TotalMinutes).Should().BeGreaterThanOrEqualTo(30);
	}

	[Fact]
	public async Task Run_NoChecksRightAfterThePush_IsNotTakenAsNoCi()
	{
		RedThenGreen();
		_github.Checks.Enqueue([]);
		_github.Checks.Enqueue([new CheckRunState("ci", CheckRunConclusion.Failed)]);

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LeftOpenCiFailed,
			"an empty list straight after a push is GitHub not having registered the runs yet, and merging then would skip CI that was about to start");
	}

	[Fact]
	public async Task Run_NoChecksOnceTheGraceHasPassed_MergesOnTheLocalGate()
	{
		RedThenGreen();
		_github.WhenQueueEmpty = [];

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.Merged);
		_waits.Sum(span => span.TotalMinutes).Should().BeGreaterThanOrEqualTo(2, "the grace period is waited out before believing there is no CI");
	}

	[Fact]
	public async Task Run_AMergeGitHubRefuses_LeavesThePullRequestOpen()
	{
		RedThenGreen();
		_github.MergeThrows = true;

		var outcome = await RunAsync(Issue());

		outcome.Kind.Should().Be(AiChangeOutcomeKind.LeftOpenMergeRefused);
		outcome.Message.Should().Contain("Head branch was modified");
		_git.DeletedRemote.Should().BeFalse();
	}

	[Fact]
	public async Task Run_StoppedPartWay_StillReturnsTheCloneToMain()
	{
		RedThenGreen();
		using var cancelled = new CancellationTokenSource();
		await cancelled.CancelAsync();

		var act = () => RunAsync(Issue(), cancelled.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
		_git.Calls.Should().Contain("return", "a stop must not leave a branch or dirty files behind");
	}
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj`
Expected: FAIL to compile, `AiChangePipeline` does not exist.

- [ ] **Step 5: Write the pipeline**

`AiChangePipeline.cs`:

```csharp
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// Takes an AI change from a clean clone to a merged pull request, or says which gate stopped it.
/// </summary>
/// <remarks>
/// Every decision is a gate that either lets the change on or names why it cannot go. Nothing reaches
/// <c>main</c> except through the merge at the end, which needs the local gate and, where the repository
/// has CI, the CI gate too. The clone is always returned to <c>main</c>, however the attempt ends,
/// because a clone left on a branch or dirty is what blocks the next fix.
/// </remarks>
public sealed class AiChangePipeline(
	IAiChangeGit git,
	IAiChangeTests tests,
	IAiChangeWork work,
	IGitHubWriteApi github,
	AiChangeOptions options)
{
	private const int _evidenceCharacters = 2_000;

	/// <summary>
	/// Runs one attempt.
	/// </summary>
	/// <param name="request">What to change.</param>
	/// <param name="say">Where progress lines go.</param>
	/// <param name="cancellationToken">Signalled when the user stops the work.</param>
	public async Task<AiChangeOutcome> RunAsync(
		AiChangeRequest request,
		Action<string> say,
		CancellationToken cancellationToken)
	{
		var refusal = await git.RefusalAsync(cancellationToken).ConfigureAwait(false);

		if (refusal is not null)
		{
			return Finish(say, new AiChangeOutcome(AiChangeOutcomeKind.Refused, refusal));
		}

		var branch = $"ai/{request.Slug}";
		var deleteRemote = false;

		await git.CreateBranchAsync(branch, cancellationToken).ConfigureAwait(false);

		try
		{
			string? redFilter = null;
			var redEvidence = string.Empty;

			if (request.RequiresRedTest)
			{
				(redFilter, redEvidence) = await FindRedTestAsync(request, say, cancellationToken).ConfigureAwait(false);

				if (redFilter is null)
				{
					await ReportNoRedTestAsync(request, cancellationToken).ConfigureAwait(false);

					return Finish(say, new AiChangeOutcome(
						AiChangeOutcomeKind.NoValidRedTest,
						"No test could be made to reproduce the report, so no fix was attempted."));
				}
			}

			var fix = await work.FixAsync(request, redFilter, cancellationToken).ConfigureAwait(false);

			if (!fix.Succeeded)
			{
				return Finish(say, Failed("the fix", fix.Summary));
			}

			var gate = await RunLocalGateAsync(redFilter, cancellationToken).ConfigureAwait(false);

			if (!gate.Passed)
			{
				return Finish(say, Failed(gate.FailedGate!, gate.Message));
			}

			var sha = await git.CommitAsync(request.Title, cancellationToken).ConfigureAwait(false);
			await git.PushBranchAsync(branch, cancellationToken).ConfigureAwait(false);

			var number = await github
				.CreatePullRequestAsync(
					request.Owner,
					request.Name,
					branch,
					"main",
					request.Title,
					BuildBody(request, redEvidence, gate.Message),
					cancellationToken)
				.ConfigureAwait(false);

			say($"Opened pull request #{number}.");

			var ci = await WaitForCiAsync(request, sha, say, cancellationToken).ConfigureAwait(false);

			if (ci is not null)
			{
				return Finish(say, ci with { PullRequestNumber = number });
			}

			try
			{
				await github
					.MergePullRequestAsync(request.Owner, request.Name, number, sha, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				return Finish(say, new AiChangeOutcome(
					AiChangeOutcomeKind.LeftOpenMergeRefused,
					$"GitHub refused the merge of #{number}: {ex.Message}",
					number));
			}

			deleteRemote = true;

			return Finish(say, new AiChangeOutcome(
				AiChangeOutcomeKind.Merged,
				$"Merged pull request #{number}.",
				number));
		}
		finally
		{
			// CancellationToken.None on purpose: a stop is the very case that needs the clone put back.
			await git.ReturnToMainAsync(branch, deleteRemote, CancellationToken.None).ConfigureAwait(false);
		}
	}

	private static AiChangeOutcome Finish(Action<string> say, AiChangeOutcome outcome)
	{
		say(outcome.Message);
		return outcome;
	}

	private static AiChangeOutcome Failed(string gate, string detail)
		=> new(AiChangeOutcomeKind.LocalGateFailed, $"The {gate} did not pass: {detail}");

	private async Task<(string? Filter, string Evidence)> FindRedTestAsync(
		AiChangeRequest request,
		Action<string> say,
		CancellationToken cancellationToken)
	{
		string? feedback = null;

		for (var attempt = 1; attempt <= options.MaxRedAttempts; attempt++)
		{
			var filter = await work.WriteRedTestAsync(request, feedback, cancellationToken).ConfigureAwait(false);

			if (filter is null)
			{
				feedback = "No test was written. Write a test that fails while the reported bug is present.";
				continue;
			}

			var run = await tests.RunOnlyAsync(filter, cancellationToken).ConfigureAwait(false);
			var verdict = RedTestGate.Validate(run);

			if (verdict.IsValidRed)
			{
				say($"Red test confirmed on attempt {attempt}.");
				return (filter, Tail(run.Output));
			}

			say($"Red test attempt {attempt} rejected: {verdict.Message.Split('\n')[0]}");
			feedback = verdict.Message;
		}

		return (null, string.Empty);
	}

	private async Task ReportNoRedTestAsync(AiChangeRequest request, CancellationToken cancellationToken)
	{
		if (request.IssueNumber is not { } issue)
		{
			return;
		}

		await github
			.CommentAsync(
				request.Owner,
				request.Name,
				issue,
				"An automated attempt could not write a test that reproduces this report, so no fix was "
					+ "attempted. That points at a gap in the tests or at a report that needs more detail; "
					+ "once there is a failing test, a fix can follow.",
				cancellationToken)
			.ConfigureAwait(false);
	}

	private sealed record GateResult(bool Passed, string? FailedGate, string Message);

	private async Task<GateResult> RunLocalGateAsync(string? redFilter, CancellationToken cancellationToken)
	{
		var changed = await git.ChangedFilesAsync(cancellationToken).ConfigureAwait(false);

		var rejected = changed.FirstOrDefault(file => !options.Limits.IsAllowedPath(file.Path));

		if (rejected is not null)
		{
			return new GateResult(false, "path allowlist", $"{rejected.Path} may not be changed.");
		}

		var productionPaths = changed.Count(file => !options.Limits.IsTestPath(file.Path));

		if (productionPaths > options.Limits.MaxPaths)
		{
			return new GateResult(false, "path cap", $"{productionPaths} files changed, and at most {options.Limits.MaxPaths} may be.");
		}

		var build = await tests.BuildAsync(cancellationToken).ConfigureAwait(false);

		if (!build.Succeeded)
		{
			return new GateResult(false, "build", build.Output);
		}

		if (redFilter is not null)
		{
			var green = await tests.RunOnlyAsync(redFilter, cancellationToken).ConfigureAwait(false);

			if (!green.Passed)
			{
				return new GateResult(false, "reproducing test", "it still fails after the fix.");
			}
		}

		var all = await tests.RunAllAsync(cancellationToken).ConfigureAwait(false);

		if (!all.Passed)
		{
			return new GateResult(false, "tests", Tail(all.Output));
		}

		var code = changed.Where(file => IsCode(file.Path)).ToList();

		if (code.Count == 0)
		{
			return new GateResult(true, null, "No code changed, so no changed-line coverage was needed.");
		}

		var cobertura = await tests.CoberturaAsync(cancellationToken).ConfigureAwait(false);

		if (cobertura is null)
		{
			return new GateResult(false, "coverage", "no coverage report could be produced.");
		}

		var coverage = ChangedLineCoverage.Evaluate(code, cobertura);

		return new GateResult(coverage.Passed, coverage.Passed ? null : "coverage", coverage.Message);
	}

	private static bool IsCode(string path)
		=> path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

	private async Task<AiChangeOutcome?> WaitForCiAsync(
		AiChangeRequest request,
		string sha,
		Action<string> say,
		CancellationToken cancellationToken)
	{
		var waited = TimeSpan.Zero;

		while (true)
		{
			var runs = await github
				.GetCheckRunsAsync(request.Owner, request.Name, sha, cancellationToken)
				.ConfigureAwait(false);

			switch (CheckRunGate.Evaluate(runs))
			{
				case CheckGateResult.Passed:
					return null;

				case CheckGateResult.Failed:
					var failed = string.Join(", ", runs.Where(run => run.Conclusion == CheckRunConclusion.Failed).Select(run => run.Name));
					return new AiChangeOutcome(AiChangeOutcomeKind.LeftOpenCiFailed, $"CI failed ({failed}); the pull request is left open.");

				case CheckGateResult.NoChecks when waited >= options.NoChecksGrace:
					say("The repository has no CI checks, so the local gate alone decides the merge.");
					return null;
			}

			if (waited >= options.CiTimeout)
			{
				return new AiChangeOutcome(
					AiChangeOutcomeKind.LeftOpenCiTimedOut,
					$"CI had not finished after {options.CiTimeout.TotalMinutes:0} minutes; the pull request is left open.");
			}

			await options.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
			waited += options.PollInterval;
		}
	}

	private static string BuildBody(AiChangeRequest request, string redEvidence, string coverageMessage)
	{
		var lines = new List<string>
		{
			request.IssueNumber is { } issue ? $"Fixes #{issue}" : $"Automated fix: {request.Title}",
			string.Empty,
			"Made and checked by this application; merged automatically once the gates passed."
		};

		if (redEvidence.Length > 0)
		{
			lines.Add(string.Empty);
			lines.Add("### Reproduction (failing before the fix)");
			lines.Add("```");
			lines.Add(redEvidence);
			lines.Add("```");
		}

		lines.Add(string.Empty);
		lines.Add("### Local gate");
		lines.Add("The build, the full suite and the reproducing test pass. " + coverageMessage);

		return string.Join('\n', lines);
	}

	private static string Tail(string output)
		=> output.Length <= _evidenceCharacters ? output : output[^_evidenceCharacters..];
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: build, then `--filter-class "*AiChangePipelineTests"`
Expected: `total: 15, succeeded: 15`. If the `Run_StoppedPartWay` test fails because the first awaited call throws before the `try`, move `CreateBranchAsync` handling so that the `try` begins before `RefusalAsync` is awaited only if the branch exists; the contract is that `ReturnToMainAsync` runs whenever a branch was created.

- [ ] **Step 7: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/AiChange PanoramicData.NugetManagement.Test/AiChangePipelineFakes.cs PanoramicData.NugetManagement.Test/AiChangePipelineTests.cs
git commit -m "feat: add the AI change pipeline with its gates and ports"
```

---

### Task 5: The git adapter over LocalRepoService

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/LocalRepoService.cs`
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/LocalRepoAiChangeGit.cs`
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/ChangedLineParser.cs`
- Test: `PanoramicData.NugetManagement.Test/ChangedLineParserTests.cs`
- Test: `PanoramicData.NugetManagement.Test/LocalRepoAiChangeGitTests.cs`

**Interfaces:**
- Consumes: `IAiChangeGit`, `ChangedFile` (Task 4); `LocalRepoService.RunCommandWithStreamingAsync` (existing, static), `LocalRepoService.GetLocalPath`, `IsWorkingTreeCleanAsync`, `GetCurrentBranchAsync`, `GetRemoteDefaultBranchAsync`, `CountCommitsBehindOriginAsync`.
- Produces: `ChangedLineParser.Parse(string unifiedZeroDiff)` returning `IReadOnlyList<ChangedFile>`; `LocalRepoAiChangeGit(LocalRepoService repos, string repoIdentity)` implementing `IAiChangeGit`.

- [ ] **Step 1: Write the failing parser test**

```csharp
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ChangedLineParser"/>: turning <c>git diff -U0</c> into changed files and lines.
/// </summary>
public class ChangedLineParserTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string Diff = """
		diff --git a/Lib/Parser.cs b/Lib/Parser.cs
		--- a/Lib/Parser.cs
		+++ b/Lib/Parser.cs
		@@ -10,0 +11,2 @@ class Parser
		+a
		+b
		@@ -20 +22 @@
		-old
		+new
		diff --git a/Lib/Gone.cs b/Lib/Gone.cs
		--- a/Lib/Gone.cs
		+++ /dev/null
		@@ -1,3 +0,0 @@
		-x
		""";

	[Fact]
	public void Parse_ReadsAddedAndModifiedLinesOnTheNewSide()
	{
		var files = ChangedLineParser.Parse(Diff);

		files.Should().ContainSingle(file => file.Path == "Lib/Parser.cs")
			.Which.Lines.Should().BeEquivalentTo(new[] { 11, 12, 22 });
	}

	[Fact]
	public void Parse_SkipsDeletedFiles()
		=> ChangedLineParser.Parse(Diff).Should().NotContain(file => file.Path == "Lib/Gone.cs",
			"a deleted file has no new-side lines to cover");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: build. Expected: FAIL to compile, `ChangedLineParser` does not exist.

- [ ] **Step 3: Write the parser**

```csharp
using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// Reads <c>git diff -U0</c> output into the new-side lines each file gained.
/// </summary>
public static partial class ChangedLineParser
{
	[GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@")]
	private static partial Regex HunkHeader();

	/// <summary>
	/// Parses a zero-context unified diff.
	/// </summary>
	/// <param name="unifiedZeroDiff">The output of <c>git diff -U0</c>.</param>
	public static IReadOnlyList<ChangedFile> Parse(string unifiedZeroDiff)
	{
		var result = new List<ChangedFile>();
		string? path = null;
		HashSet<int>? lines = null;

		foreach (var line in unifiedZeroDiff.Split('\n').Select(l => l.TrimEnd('\r')))
		{
			if (line.StartsWith("+++ ", StringComparison.Ordinal))
			{
				var target = line[4..];
				path = target == "/dev/null" ? null : target.StartsWith("b/", StringComparison.Ordinal) ? target[2..] : target;
				lines = path is null ? null : [];

				if (path is not null)
				{
					result.Add(new ChangedFile(path, lines!));
				}

				continue;
			}

			var match = HunkHeader().Match(line);

			if (!match.Success || lines is null)
			{
				continue;
			}

			var start = int.Parse(match.Groups["start"].Value, System.Globalization.CultureInfo.InvariantCulture);
			var count = match.Groups["count"].Success
				? int.Parse(match.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture)
				: 1;

			for (var offset = 0; offset < count; offset++)
			{
				lines.Add(start + offset);
			}
		}

		return result;
	}
}
```

- [ ] **Step 4: Run to verify it passes**

Run: build, then `--filter-class "*ChangedLineParserTests"`. Expected: `total: 2, succeeded: 2`.

- [ ] **Step 5: Write the failing adapter tests against a real temporary repository**

```csharp
using PanoramicData.NugetManagement.Web.Services;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="LocalRepoAiChangeGit"/> against a real clone of a real (local, bare) remote.
/// </summary>
public sealed class LocalRepoAiChangeGitTests : TestWithOutput, IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "aichange-" + Guid.NewGuid().ToString("N"));
	private readonly string _clone;

	public LocalRepoAiChangeGitTests(ITestOutputHelper output)
		: base(output)
	{
		var remote = Path.Combine(_root, "remote.git");
		var seed = Path.Combine(_root, "seed");
		_clone = Path.Combine(_root, "clone");
		Directory.CreateDirectory(_root);

		Git(_root, "init", "--bare", "-b", "main", remote);
		Git(_root, "clone", remote, seed);
		Git(seed, "config", "user.email", "t@t");
		Git(seed, "config", "user.name", "t");
		File.WriteAllText(Path.Combine(seed, "a.cs"), "one\n");
		Git(seed, "add", "-A");
		Git(seed, "commit", "-m", "init");
		Git(seed, "push", "origin", "main");
		Git(_root, "clone", remote, _clone);
		Git(_clone, "config", "user.email", "t@t");
		Git(_clone, "config", "user.name", "t");
	}

	public void Dispose()
	{
		foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(_root, recursive: true);
	}

	private static void Git(string directory, params string[] arguments)
	{
		var (ok, text) = LocalRepoService.RunCommandWithStreamingAsync(directory, "git", arguments).GetAwaiter().GetResult();
		ok.Should().BeTrue($"git {string.Join(' ', arguments)} should work: {text}");
	}

	private LocalRepoAiChangeGit Adapter() => new(_clone);

	[Fact]
	public async Task Refusal_IsNullForACleanCloneOnMain()
		=> (await Adapter().RefusalAsync(TestContext.Current.CancellationToken)).Should().BeNull();

	[Fact]
	public async Task Refusal_NamesADirtyClone()
	{
		File.WriteAllText(Path.Combine(_clone, "a.cs"), "changed\n");

		(await Adapter().RefusalAsync(TestContext.Current.CancellationToken)).Should().Contain("uncommitted");
	}

	[Fact]
	public async Task ChangedFilesAsync_SeesCommittedAndUncommittedChangesAgainstMain()
	{
		var git = Adapter();
		await git.CreateBranchAsync("ai/x", TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(_clone, "a.cs"), "one\ntwo\n");

		var changed = await git.ChangedFilesAsync(TestContext.Current.CancellationToken);

		changed.Should().ContainSingle(file => file.Path == "a.cs")
			.Which.Lines.Should().BeEquivalentTo(new[] { 2 });
	}

	[Fact]
	public async Task ReturnToMainAsync_DiscardsWorkAndDeletesTheBranch()
	{
		var git = Adapter();
		await git.CreateBranchAsync("ai/x", TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(_clone, "a.cs"), "scribble\n");

		await git.ReturnToMainAsync("ai/x", deleteRemoteBranch: false, TestContext.Current.CancellationToken);

		File.ReadAllText(Path.Combine(_clone, "a.cs")).Should().Be("one\n");
		(await Adapter().RefusalAsync(TestContext.Current.CancellationToken)).Should().BeNull("the clone is clean and back on main");
	}

	[Fact]
	public async Task CommitAndPush_PublishesTheBranch()
	{
		var git = Adapter();
		await git.CreateBranchAsync("ai/x", TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(_clone, "a.cs"), "one\ntwo\n");

		var sha = await git.CommitAsync("change", TestContext.Current.CancellationToken);
		await git.PushBranchAsync("ai/x", TestContext.Current.CancellationToken);

		sha.Should().HaveLength(40);
		var (_, remoteBranches) = await LocalRepoService.RunCommandWithStreamingAsync(_clone, "git", ["ls-remote", "--heads", "origin"]);
		remoteBranches.Should().Contain("refs/heads/ai/x");
	}
}
```

- [ ] **Step 6: Run to verify it fails**

Run: build. Expected: FAIL to compile, `LocalRepoAiChangeGit` does not exist.

- [ ] **Step 7: Write the adapter**

This adapter takes the clone's path directly (not the repo identity), so it can be tested against a temp directory and constructed by the executors from `row.LocalPath`. It uses the static `LocalRepoService.RunCommandWithStreamingAsync` and the transient-failure retry added earlier for the network steps; make `RunNetworkGitWithRetryAsync` `internal` in `LocalRepoService` so it can be reused.

In `LocalRepoService.cs` change `private static async Task<(bool Success, string Output)> RunNetworkGitWithRetryAsync(` to `internal static async Task<(bool Success, string Output)> RunNetworkGitWithRetryAsync(`.

`LocalRepoAiChangeGit.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// <see cref="IAiChangeGit"/> over a clone on disk.
/// </summary>
/// <param name="clonePath">The clone's working directory.</param>
public sealed class LocalRepoAiChangeGit(string clonePath) : IAiChangeGit
{
	/// <inheritdoc />
	public async Task<string?> RefusalAsync(CancellationToken cancellationToken)
	{
		var (ok, branch) = await GitAsync(["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);

		if (!ok || branch.Trim() != "main")
		{
			return $"the clone is on '{branch.Trim()}', not main.";
		}

		var (statusOk, status) = await GitAsync(["status", "--porcelain"], cancellationToken).ConfigureAwait(false);

		if (!statusOk || status.Trim().Length > 0)
		{
			return "the clone has uncommitted changes.";
		}

		(ok, var output) = await LocalRepoService.RunNetworkGitWithRetryAsync(clonePath, ["pull", "--ff-only"], null, cancellationToken).ConfigureAwait(false);

		return ok ? null : $"main could not be fast-forwarded: {output}";
	}

	/// <inheritdoc />
	public async Task CreateBranchAsync(string branch, CancellationToken cancellationToken)
		=> await RequireAsync(["checkout", "-b", branch], cancellationToken).ConfigureAwait(false);

	/// <inheritdoc />
	public async Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(CancellationToken cancellationToken)
	{
		// Untracked files are added intent-to-add so a brand-new test file shows up in the diff.
		await GitAsync(["add", "-N", "."], cancellationToken).ConfigureAwait(false);

		var diff = await RequireAsync(["diff", "-U0", "main"], cancellationToken).ConfigureAwait(false);

		return ChangedLineParser.Parse(diff);
	}

	/// <inheritdoc />
	public async Task<string> CommitAsync(string message, CancellationToken cancellationToken)
	{
		await RequireAsync(["add", "-A"], cancellationToken).ConfigureAwait(false);
		await RequireAsync(["commit", "-m", message], cancellationToken).ConfigureAwait(false);

		return (await RequireAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).Trim();
	}

	/// <inheritdoc />
	public async Task PushBranchAsync(string branch, CancellationToken cancellationToken)
	{
		var (ok, output) = await LocalRepoService
			.RunNetworkGitWithRetryAsync(clonePath, ["push", "-u", "origin", branch], null, cancellationToken)
			.ConfigureAwait(false);

		if (!ok)
		{
			throw new InvalidOperationException($"git push failed: {output}");
		}
	}

	/// <inheritdoc />
	public async Task ReturnToMainAsync(string branch, bool deleteRemoteBranch, CancellationToken cancellationToken)
	{
		await GitAsync(["reset", "--hard"], cancellationToken).ConfigureAwait(false);
		await GitAsync(["clean", "-fd"], cancellationToken).ConfigureAwait(false);
		await GitAsync(["checkout", "main"], cancellationToken).ConfigureAwait(false);
		await GitAsync(["branch", "-D", branch], cancellationToken).ConfigureAwait(false);

		if (deleteRemoteBranch)
		{
			await LocalRepoService
				.RunNetworkGitWithRetryAsync(clonePath, ["push", "origin", "--delete", branch], null, cancellationToken)
				.ConfigureAwait(false);
		}

		await LocalRepoService
			.RunNetworkGitWithRetryAsync(clonePath, ["pull", "--ff-only"], null, cancellationToken)
			.ConfigureAwait(false);
	}

	private Task<(bool Success, string Output)> GitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
		=> LocalRepoService.RunCommandWithStreamingAsync(clonePath, "git", arguments, null, cancellationToken);

	private async Task<string> RequireAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		var (ok, output) = await GitAsync(arguments, cancellationToken).ConfigureAwait(false);

		return ok
			? output
			: throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}");
	}
}
```

- [ ] **Step 8: Run to verify it passes**

Run: build, then `--filter-class "*LocalRepoAiChangeGitTests"` and `--filter-class "*ChangedLineParserTests"`.
Expected: `total: 5, succeeded: 5` and `total: 2, succeeded: 2`. These tests shell out to `git`, which every dev and CI machine here has.

- [ ] **Step 9: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services PanoramicData.NugetManagement.Test
git commit -m "feat: add the git adapter and diff parser for the AI change pipeline"
```

---

### Task 6: The test runner adapter with coverage

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/LocalRepoService.cs` (add `RunTestsWithCoverageAsync`)
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/DashboardAiChangeTests.cs`
- Test: `PanoramicData.NugetManagement.Test/AiChangeCoverageArgumentsTests.cs`

**Interfaces:**
- Consumes: `IAiChangeTests`, `TestRun`, `IssueFixCheckStep` (Tasks 3, 4); `DashboardService.BuildAsync(row, onOutput, ct)` and `LocalRepoService.GetConfiguredTestProjects` (existing).
- Produces: `LocalRepoService.BuildCoverageArguments(string testProject, string outputFile, string? filter)` (public static, returns `IReadOnlyList<string>`), `DashboardAiChangeTests(DashboardService dashboard, LocalRepoService repos, RepositoryDashboardRow row)` implementing `IAiChangeTests`.

- [ ] **Step 1: Write the failing test for the argument builder**

```csharp
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the arguments of a coverage-collecting test run.
/// </summary>
public class AiChangeCoverageArgumentsTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void BuildCoverageArguments_AsksForCoberturaAtTheGivenPath()
	{
		var args = LocalRepoService.BuildCoverageArguments("Lib.Test/Lib.Test.csproj", @"C:\tmp\c.xml", null);

		args.Should().ContainInOrder("test", "Lib.Test/Lib.Test.csproj");
		args.Should().Contain("--coverage");
		args.Should().ContainInOrder("--coverage-output-format", "cobertura");
		args.Should().ContainInOrder("--coverage-output", @"C:\tmp\c.xml");
	}

	[Fact]
	public void BuildCoverageArguments_AddsAFilterWhenGiven()
		=> LocalRepoService.BuildCoverageArguments("p.csproj", "c.xml", "FullyQualifiedName~Parser")
			.Should().ContainInOrder("--filter", "FullyQualifiedName~Parser");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: build. Expected: FAIL to compile, `BuildCoverageArguments` does not exist.

- [ ] **Step 3: Implement**

Add to `LocalRepoService` (near `RunTestsAsync`):

```csharp
	/// <summary>
	/// The arguments for running one test project under the code-coverage collector.
	/// </summary>
	/// <param name="testProject">The test project path, relative to the clone.</param>
	/// <param name="outputFile">Where the Cobertura report is to be written.</param>
	/// <param name="filter">A test filter, or null for every test.</param>
	public static IReadOnlyList<string> BuildCoverageArguments(string testProject, string outputFile, string? filter)
	{
		var args = new List<string>
		{
			"test", testProject, "--no-restore", "--coverage",
			"--coverage-output-format", "cobertura",
			"--coverage-output", outputFile
		};

		if (!string.IsNullOrWhiteSpace(filter))
		{
			args.Add("--filter");
			args.Add(filter);
		}

		return args;
	}

	/// <summary>
	/// Runs the repository's tests with coverage and returns the Cobertura report.
	/// </summary>
	/// <param name="repoIdentity">The repository.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The report's XML, or null if the run produced none.</returns>
	public async Task<string?> RunTestsWithCoverageAsync(string repoIdentity, CancellationToken cancellationToken = default)
	{
		var path = GetLocalPath(repoIdentity);
		var projects = GetConfiguredTestProjects(path);
		var reports = new List<string>();

		foreach (var (projectPath, _) in projects)
		{
			var output = Path.Combine(Path.GetTempPath(), $"aichange-cov-{Guid.NewGuid():N}.xml");

			await RunCommandWithStreamingAsync(path, "dotnet", BuildCoverageArguments(projectPath, output, null), null, cancellationToken).ConfigureAwait(false);

			if (File.Exists(output))
			{
				reports.Add(await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false));
				File.Delete(output);
			}
		}

		// Wrapped in one root so several test projects' reports read as a single document;
		// ChangedLineCoverage reads every <class> beneath whatever the root is.
		return reports.Count == 0
			? null
			: "<reports>" + string.Concat(reports.Select(StripXmlDeclaration)) + "</reports>";
	}

	private static string StripXmlDeclaration(string xml)
		=> System.Text.RegularExpressions.Regex.Replace(xml, @"<\?xml[^>]*\?>", string.Empty);
```

`DashboardAiChangeTests.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// <see cref="IAiChangeTests"/> over the dashboard's build and test plumbing.
/// </summary>
public sealed class DashboardAiChangeTests(
	DashboardService dashboard,
	LocalRepoService repos,
	RepositoryDashboardRow row,
	string repoIdentity) : IAiChangeTests
{
	/// <inheritdoc />
	public async Task<IssueFixCheckStep> BuildAsync(CancellationToken cancellationToken)
	{
		var lines = new List<string>();
		await dashboard.BuildAsync(row, lines.Add, cancellationToken).ConfigureAwait(false);

		return new IssueFixCheckStep(row.Status == PackageStatus.BuildSucceeded, string.Join('\n', lines));
	}

	/// <inheritdoc />
	public async Task<TestRun> RunAllAsync(CancellationToken cancellationToken)
	{
		var lines = new List<string>();
		await dashboard.RunTestsAsync(row, lines.Add, cancellationToken).ConfigureAwait(false);

		return new TestRun(row.Status == PackageStatus.TestsPassed, string.Join('\n', lines));
	}

	/// <inheritdoc />
	public async Task<TestRun> RunOnlyAsync(string testFilter, CancellationToken cancellationToken)
	{
		var path = repos.GetLocalPath(repoIdentity);
		var passed = true;
		var output = new List<string>();

		foreach (var (projectPath, _) in LocalRepoService.GetConfiguredTestProjects(path))
		{
			var (ok, text) = await LocalRepoService
				.RunCommandWithStreamingAsync(path, "dotnet", ["test", projectPath, "--no-restore", "--filter", testFilter], null, cancellationToken)
				.ConfigureAwait(false);

			passed &= ok;
			output.Add(text);
		}

		return new TestRun(passed, string.Join('\n', output));
	}

	/// <inheritdoc />
	public Task<string?> CoberturaAsync(CancellationToken cancellationToken)
		=> repos.RunTestsWithCoverageAsync(repoIdentity, cancellationToken);
}
```

The implementer must check two things in `LocalRepoService.cs` before this compiles: whether `GetConfiguredTestProjects` is `static` and `public` (make it `internal static` if it is private), and the exact tuple shape it returns (it is used as `foreach (var (projectPath, projectConfig) in testProjects)` in `RunTestsAsync`). Adjust the deconstruction to match.

- [ ] **Step 4: Run to verify it passes**

Run: build, then `--filter-class "*AiChangeCoverageArgumentsTests"`. Expected: `total: 2, succeeded: 2`.
Then verify the coverage run for real against one clone (this is the part a unit test cannot prove): in a scratch clone with a test project, run `dotnet test <project> --no-restore --coverage --coverage-output-format cobertura --coverage-output C:\temp\c.xml` and confirm `c.xml` is written and contains `<class filename=`. If the report lands in a different place or format, fix `BuildCoverageArguments` and its test.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services PanoramicData.NugetManagement.Test
git commit -m "feat: run a repository's tests with coverage for the AI change pipeline"
```

---

### Task 7: The model-facing work, the red-test prompt and the test-project guide

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/AiTestGuide.cs`
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/RedTestPrompt.cs`
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/AiSessionChangeWork.cs`
- Test: `PanoramicData.NugetManagement.Test/AiTestGuideTests.cs`
- Test: `PanoramicData.NugetManagement.Test/RedTestPromptTests.cs`

**Interfaces:**
- Consumes: `IAiChangeWork`, `AiChangeRequest` (Task 4); `AiFixSession`, `AiFixRequest`, `AiFixOutcome`, `AiRuleCheck` (existing; `AiFixSession.RunAsync(AiFixRequest, Func<CancellationToken, Task<AiRuleCheck>>, CancellationToken)`).
- Produces: `AiTestGuide.Text` (string constant), `RedTestPrompt.Build(AiChangeRequest, string issueText, string? feedback)`, `AiSessionChangeWork` implementing `IAiChangeWork`.

- [ ] **Step 1: Write the failing tests**

`AiTestGuideTests.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AiTestGuide"/>: the guidance that must agree with what the TST rules grade.
/// </summary>
public class AiTestGuideTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData("xunit v3")]
	[InlineData("Microsoft.Testing.Platform")]
	[InlineData("OutputType")]
	[InlineData("AwesomeAssertions")]
	[InlineData("FluentAssertions")]
	[InlineData("failSkips")]
	[InlineData("Microsoft.Testing.Extensions.CodeCoverage")]
	[InlineData("Directory.Packages.props")]
	public void Text_CoversWhatTheTestRulesCheck(string requirement)
		=> AiTestGuide.Text.Should().Contain(requirement);

	[Fact]
	public void Text_TellsTheModelNotToUseTheVsTestAdapter()
		=> AiTestGuide.Text.Should().Contain("xunit.runner.visualstudio");
}
```

`RedTestPromptTests.cs`:

```csharp
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="RedTestPrompt"/>.
/// </summary>
public class RedTestPromptTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly AiChangeRequest Request = new("o", "r", AiChangeKind.IssueFix, "issue-42-a", "Fix #42", 42, true);

	[Fact]
	public void Build_StatesThatTheTestMustFailBeforeAnyFix()
		=> RedTestPrompt.Build(Request, "Parser drops the last row", null)
			.Should().Contain("fail").And.Contain("Parser drops the last row");

	[Fact]
	public void Build_IncludesTheTestGuide()
		=> RedTestPrompt.Build(Request, "x", null).Should().Contain(AiTestGuide.Text);

	[Fact]
	public void Build_FeedsBackWhyThePreviousAttemptWasRejected()
		=> RedTestPrompt.Build(Request, "x", "The test passes on the unfixed code")
			.Should().Contain("The test passes on the unfixed code");

	[Fact]
	public void Build_AsksForTheTestNameAsTheLastLine()
		=> RedTestPrompt.Build(Request, "x", null).Should().Contain("FILTER:");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: build. Expected: FAIL to compile.

- [ ] **Step 3: Write the guide, the prompt and the work adapter**

`AiTestGuide.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// How to write tests in this estate, given to the model whenever it may write or create a test project.
/// </summary>
/// <remarks>
/// This restates what the TST rules grade (TST-01 to TST-06 and TST-08), so a project the model builds
/// from it passes them. If a rule changes, <c>AiTestGuideTests</c> is where the drift shows.
/// </remarks>
public static class AiTestGuide
{
	/// <summary>The guide, as it appears in a prompt.</summary>
	public const string Text = """
		Testing conventions for this repository (these are graded by automated rules):

		- Use xunit v3. The test project sets <OutputType>Exe</OutputType>, because xunit v3 builds its own runner executable.
		- Run tests with Microsoft.Testing.Platform: global.json contains "test": { "runner": "Microsoft.Testing.Platform" }.
		- Do not reference xunit.runner.visualstudio. It is the old VSTest adapter, has no role under Microsoft.Testing.Platform, and breaks restore under central package management.
		- Reference Microsoft.NET.Test.Sdk and Microsoft.Testing.Extensions.CodeCoverage.
		- Assert with AwesomeAssertions (using AwesomeAssertions;). Never use FluentAssertions.
		- Put an xunit.runner.json next to the tests that contains { "failSkips": true } and copy it to the output directory, so a skipped test fails instead of hiding.
		- Package versions live in Directory.Packages.props; add PackageVersion entries there and reference packages without a Version in the project file.
		- One test class per class under test, named <ClassUnderTest>Tests. Test method names say the behaviour, for example Parse_DropsNothingWhenTheLastRowIsBlank.
		- A test that reproduces a bug must fail by an assertion while the bug is present. A test that does not compile, or that matches no test, proves nothing.
		- Use tabs for indentation in C# and project files.
		""";
}
```

`RedTestPrompt.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// The task given to the model when it must reproduce a bug before fixing it.
/// </summary>
public static class RedTestPrompt
{
	/// <summary>
	/// Builds the prompt.
	/// </summary>
	/// <param name="request">What is being fixed.</param>
	/// <param name="issueText">The report, as written.</param>
	/// <param name="feedback">Why the previous attempt was rejected, or null on the first.</param>
	public static string Build(AiChangeRequest request, string issueText, string? feedback)
	{
		var lines = new List<string>
		{
			$"Write ONE automated test that reproduces this reported problem in {request.Owner}/{request.Name}.",
			"The test must fail while the problem is present. Do not change any production code in this step.",
			string.Empty,
			"The report:",
			issueText,
			string.Empty,
			AiTestGuide.Text
		};

		if (feedback is not null)
		{
			lines.Add(string.Empty);
			lines.Add("Your previous attempt was rejected:");
			lines.Add(feedback);
		}

		lines.Add(string.Empty);
		lines.Add("When the test is written and compiles, finish with a last line of the form:");
		lines.Add("FILTER: <a dotnet test --filter expression that selects only your new test>");

		return string.Join('\n', lines);
	}
}
```

`AiSessionChangeWork.cs`:

```csharp
namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// <see cref="IAiChangeWork"/> over <see cref="AiFixSession"/>.
/// </summary>
/// <remarks>
/// The session already knows how to get a model from a task to a checked result; this only supplies the
/// task and the check. Writing the red test is checked by the model compiling it (the build tool), and
/// the pipeline, not this class, judges whether it is genuinely red.
/// </remarks>
/// <param name="session">The model loop.</param>
/// <param name="issueText">The report as written, for issue fixes.</param>
/// <param name="fixTask">The task for the fix step.</param>
/// <param name="systemPrompt">The system prompt.</param>
/// <param name="fixCheck">Re-evaluates the fix against the clone (a rule check, or build and tests).</param>
/// <param name="buildCheck">Checks the new test compiles.</param>
public sealed class AiSessionChangeWork(
	AiFixSession session,
	string issueText,
	string fixTask,
	string systemPrompt,
	Func<CancellationToken, Task<AiRuleCheck>> fixCheck,
	Func<CancellationToken, Task<AiRuleCheck>> buildCheck) : IAiChangeWork
{
	private const string _filterMarker = "FILTER:";

	/// <inheritdoc />
	public async Task<string?> WriteRedTestAsync(
		AiChangeRequest request,
		string? feedback,
		CancellationToken cancellationToken)
	{
		var outcome = await session
			.RunAsync(
				new AiFixRequest(
					$"{request.Owner}/{request.Name}",
					request.Slug,
					request.Title,
					RedTestPrompt.Build(request, issueText, feedback),
					systemPrompt),
				buildCheck,
				cancellationToken)
			.ConfigureAwait(false);

		if (!outcome.Succeeded)
		{
			return null;
		}

		var line = outcome.Summary
			.Split('\n')
			.LastOrDefault(l => l.TrimStart().StartsWith(_filterMarker, StringComparison.Ordinal));

		return line?.Trim()[_filterMarker.Length..].Trim() is { Length: > 0 } filter ? filter : null;
	}

	/// <inheritdoc />
	public async Task<AiFixOutcome> FixAsync(
		AiChangeRequest request,
		string? redTestFilter,
		CancellationToken cancellationToken)
	{
		var task = redTestFilter is null
			? fixTask
			: $"{fixTask}\n\nThe test selected by `--filter {redTestFilter}` currently fails and reproduces the report. "
				+ "Change production code until it passes and every other test still passes. Do not edit that test.";

		return await session
			.RunAsync(
				new AiFixRequest($"{request.Owner}/{request.Name}", request.Slug, request.Title, task, systemPrompt),
				fixCheck,
				cancellationToken)
			.ConfigureAwait(false);
	}
}
```

The implementer must check the real `AiFixRequest` constructor parameter order at `AiFixSession.cs:41` (it is `AiFixRequest(string Repository, string RuleId, string RuleName, string Task, string SystemPrompt)` per the executor's call in `FixWithAiRuleAsync`) and confirm that `AiFixOutcome.Summary` carries the model's `finish` text. If the summary does not carry the final line, add a `FinishText` to `AiFixOutcome` and a test in `AiFixSessionTests` before relying on it.

- [ ] **Step 4: Run to verify it passes**

Run: build, then `--filter-class "*AiTestGuideTests"` and `--filter-class "*RedTestPromptTests"`.
Expected: `total: 9, succeeded: 9` and `total: 4, succeeded: 4`.

- [ ] **Step 5: Commit**

```bash
git add PanoramicData.NugetManagement.Web/Services/AiChange PanoramicData.NugetManagement.Test
git commit -m "feat: add the red-test prompt, the test guide and the model-facing work adapter"
```

---

### Task 8: Wire rule fixes through the pipeline

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/WorkExecutors.cs` (`FixWithAiRuleAsync`, around lines 888 to 1068)
- Create: `PanoramicData.NugetManagement.Web/Services/AiChange/AiChangePipelineFactory.cs`
- Test: `PanoramicData.NugetManagement.Test/AiChangePipelineFactoryTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 to 7; `IssuePathScreen` and `IssueAutonomyGate.MaxUnattendedPaths` (existing).
- Produces: `AiChangePipelineFactory.Create(IAiChangeGit git, IAiChangeTests tests, IAiChangeWork work, IGitHubWriteApi github, Func<string, bool> isAllowedPath)` returning `AiChangePipeline`; `AiChangePipelineFactory.IsTestPath(string)`; `AiChangePipelineFactory.Slug(string label, string unique)`.

- [ ] **Step 1: Write the failing test**

```csharp
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the small decisions <see cref="AiChangePipelineFactory"/> makes on the pipeline's behalf.
/// </summary>
public class AiChangePipelineFactoryTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Theory]
	[InlineData("Lib.Test/ParserTests.cs", true)]
	[InlineData("Lib.Tests/ParserTests.cs", true)]
	[InlineData("src/ParserTests.cs", true)]
	[InlineData("Lib/Parser.cs", false)]
	[InlineData("Lib/Contest.cs", false)]
	public void IsTestPath_RecognisesTestFiles(string path, bool expected)
		=> AiChangePipelineFactory.IsTestPath(path).Should().Be(expected);

	[Theory]
	[InlineData("rule CI-15", "a1b2", "rule-ci-15-a1b2")]
	[InlineData("issue 42", "c3d4", "issue-42-c3d4")]
	public void Slug_IsLowerCaseBranchSafeAndUnique(string label, string unique, string expected)
		=> AiChangePipelineFactory.Slug(label, unique).Should().Be(expected);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: build. Expected: FAIL to compile.

- [ ] **Step 3: Implement the factory**

```csharp
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// Builds the pipeline with this application's limits.
/// </summary>
public static partial class AiChangePipelineFactory
{
	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex Unsafe();

	/// <summary>Creates a pipeline.</summary>
	public static AiChangePipeline Create(
		IAiChangeGit git,
		IAiChangeTests tests,
		IAiChangeWork work,
		IGitHubWriteApi github,
		Func<string, bool> isAllowedPath)
		=> new(git, tests, work, github, new AiChangeOptions
		{
			Limits = new AiChangeLimits(isAllowedPath, IsTestPath, IssueAutonomyGate.MaxUnattendedPaths)
		});

	/// <summary>
	/// Whether a repository-relative path is a test file.
	/// </summary>
	/// <param name="path">The path.</param>
	public static bool IsTestPath(string path)
	{
		var normalised = path.Replace('\\', '/');
		var segments = normalised.Split('/');

		return segments.Any(segment => segment.EndsWith(".Test", StringComparison.Ordinal) || segment.EndsWith(".Tests", StringComparison.Ordinal))
			|| segments[^1].EndsWith("Tests.cs", StringComparison.Ordinal);
	}

	/// <summary>
	/// A unique, branch-safe label.
	/// </summary>
	/// <param name="label">What it is for, for example <c>rule CI-15</c> or <c>issue 42</c>.</param>
	/// <param name="unique">A short unique suffix.</param>
	public static string Slug(string label, string unique)
		=> Unsafe().Replace(label.ToLowerInvariant(), "-").Trim('-') + "-" + unique;
}
```

If `IssueAutonomyGate.MaxUnattendedPaths` is not accessible (it is a `const`/`static` in `IssueAutonomyGate.cs:45`), make it `public`.

- [ ] **Step 4: Replace the rule-fix tail in `FixWithAiRuleAsync`**

In `FixWithAiRuleAsync` (WorkExecutors.cs), the current success handling re-evaluates, reports and says "Review the working tree before committing". Replace everything from `var outcome = await session.RunAsync(...)` through the end of the `try` block with a pipeline call. The AI session is no longer run directly: it becomes the `AiSessionChangeWork` the pipeline drives.

Replace that section with:

```csharp
				var work = new AiSessionChangeWork(
					session,
					issueText: string.Empty,
					fixTask: request.Task,
					systemPrompt: request.SystemPrompt,
					fixCheck: token => remotelyGraded
						? CheckLocalChangeAsync(row, toolbox, token)
						: ReEvaluateAsync(row, ruleId, token),
					buildCheck: token => CheckLocalChangeAsync(row, toolbox, token));

				var identity = RepoIdentity(row);
				var pipeline = AiChangePipelineFactory.Create(
					new LocalRepoAiChangeGit(row.LocalPath),
					new DashboardAiChangeTests(dashboard, localRepo, row, identity),
					work,
					githubWrite,
					path => !IssuePathScreen.IsRefused(path));

				var change = new AiChangeRequest(
					row.Organization!,
					row.RepositoryName,
					AiChangeKind.RuleFix,
					AiChangePipelineFactory.Slug($"rule {ruleId}", Guid.NewGuid().ToString("N")[..6]),
					$"Fix {ruleId}: {result.RuleName}",
					IssueNumber: null,
					RequiresRedTest: false);

				var delivered = await pipeline.RunAsync(change, Say, cancellationToken).ConfigureAwait(false);
				succeeded = delivered.Kind is AiChangeOutcomeKind.Merged or AiChangeOutcomeKind.LeftOpenCiFailed or AiChangeOutcomeKind.LeftOpenCiTimedOut or AiChangeOutcomeKind.LeftOpenMergeRefused;

				await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
				cache.UpsertRow(row);
```

The implementer must confirm these names in the real code before writing them, and adapt: `localRepo` and `githubWrite` are the executor's injected `LocalRepoService` and `IGitHubWriteApi` (look at the primary constructor of `WorkExecutors`; add the two parameters and register them if missing), `RepoIdentity(row)` is `DashboardService`'s private helper (use `row.RepositoryFullName` if the executor has no equivalent, and pass that), and `IssuePathScreen.IsRefused` stands for whatever the existing screen exposes: open `IssuePathScreen.cs` and use its real "is this path writable" predicate (negated if it reports refusal). The `finally` block that calls `RevertPartAppliedFixAsync` is now redundant for pipeline outcomes because the pipeline returns the clone to `main`; remove the call but keep the `OperationCanceledException` catch's `Say`.

Delete the "Review the working tree before committing" messages in this method. The outcome line comes from the pipeline's own `say`.

- [ ] **Step 5: Run to verify**

Run: build, then `--filter-class "*AiChangePipelineFactoryTests"`, `--filter-class "*AiFixObservabilityTests"`, `--filter-class "*AiFixSessionTests"`, `--filter-class "*WorkExecutorsTests"`.
Expected: all pass. Any test that asserted the old "review the working tree" text must be updated to the pipeline's wording in this same task.

- [ ] **Step 6: Commit**

```bash
git add PanoramicData.NugetManagement.Web PanoramicData.NugetManagement.Test
git commit -m "feat: deliver rule fixes through the AI change pipeline"
```

---

### Task 9: Wire issue fixes through the pipeline and retire the oracle

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Services/WorkExecutors.cs` (`FixWithAiIssueAsync`, from line 1922)
- Modify: `PanoramicData.NugetManagement.Web/Services/IssueAutonomyGate.cs`
- Delete: `PanoramicData.NugetManagement.Web/Services/IssueFixOracle.cs` and `PanoramicData.NugetManagement.Test/IssueFixOracleTests.cs`, after `IssueFixCheckStep` is moved
- Modify: `PanoramicData.NugetManagement.Web/Services/AiChange/AiChangePorts.cs` (host `IssueFixCheckStep`)
- Test: `PanoramicData.NugetManagement.Test/IssueAutonomyGateTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 8.
- Produces: an `IssueFixCheckStep` record living beside the ports; the issue executor delivering through `AiChangePipeline` with `RequiresRedTest: true`.

- [ ] **Step 1: Move `IssueFixCheckStep`**

`IssueFixCheckStep` is the only part of `IssueFixOracle.cs` the pipeline keeps. Cut the record

```csharp
public sealed record IssueFixCheckStep(bool Succeeded, string Output);
```

with its XML doc comment out of `IssueFixOracle.cs` and paste it at the end of `AiChangePorts.cs` in the `PanoramicData.NugetManagement.Web.Services.AiChange` namespace. In `AiChangePorts.cs` add `using PanoramicData.NugetManagement.Web.Services;` is not needed (same project, different namespace is fine), but `DashboardAiChangeTests.cs` and the test fakes reference `IssueFixCheckStep` through `using PanoramicData.NugetManagement.Web.Services;`; change those to `using PanoramicData.NugetManagement.Web.Services.AiChange;`.

- [ ] **Step 2: Write the failing gate test**

Add to `IssueAutonomyGateTests.cs`, following its existing construction of a verdict (read the file first and copy its helper):

```csharp
	[Fact]
	public void RequiresRedTest_IsTrueForEveryUnattendedFix()
		=> IssueAutonomyGate.RequiresRedTest.Should().BeTrue(
			"an issue fix is accepted on a test that failed before it and passes after, never on prose");
```

- [ ] **Step 3: Run to verify it fails**

Run: build. Expected: FAIL to compile, `RequiresRedTest` does not exist.

- [ ] **Step 4: Implement**

In `IssueAutonomyGate.cs` add beside `MaxUnattendedPaths`:

```csharp
	/// <summary>
	/// Whether an unattended issue fix must be reproduced by a test that fails first.
	/// </summary>
	/// <remarks>
	/// Always. This is what replaced "the existing tests still pass", which could not say whether the
	/// reported problem was solved.
	/// </remarks>
	public const bool RequiresRedTest = true;
```

Replace the body of `FixWithAiIssueAsync` from where `IssueFixOracle` is constructed through the success and failure messages (around lines 1700 to 1735) with the same pipeline construction as Task 8, with these differences:

```csharp
				var work = new AiSessionChangeWork(
					session,
					issueText: IssueThreadText(issue),
					fixTask: IssueFixPrompt.BuildTask(issue, brief),
					systemPrompt: IssueFixPrompt.SystemPrompt,
					fixCheck: token => CheckBuildAndTestsAsync(row, token),
					buildCheck: token => CheckBuildAsync(row, token));

				var change = new AiChangeRequest(
					row.Organization!,
					row.RepositoryName,
					AiChangeKind.IssueFix,
					AiChangePipelineFactory.Slug($"issue {issueNumber}", Guid.NewGuid().ToString("N")[..6]),
					$"Fix #{issueNumber}: {issue.Title}",
					issueNumber,
					RequiresRedTest: IssueAutonomyGate.RequiresRedTest);
```

The implementer must adapt to the real code: `IssueFixPrompt.BuildTask` and `IssueFixPrompt.SystemPrompt` are the names used by the existing issue-fix prompt (open `IssueFixPrompt.cs` and use its real members), `IssueThreadText(issue)` is whatever the existing code uses to render the issue for the model, and `CheckBuildAndTestsAsync` / `CheckBuildAsync` wrap the existing build and test callbacks that were passed to `IssueFixOracle` (`dashboard.BuildAsync` and `dashboard.RunTestsAsync`), returning `AiRuleCheck`. Keep `IssuePathScreen` and `IssueFixSnapshot`: the screen becomes the pipeline's `isAllowedPath`, and the snapshot is no longer needed because the pipeline restores the clone, so remove the snapshot calls and `IssueFixSnapshot` if nothing else uses it (grep first; delete its tests only if the class goes).

Delete `IssueFixOracle.cs` and `IssueFixOracleTests.cs`. Update any test or code that referenced the oracle.

- [ ] **Step 5: Run to verify**

Run: build (0 warnings), then `--filter-class "*IssueAutonomyGateTests"`, `--filter-class "*IssuePathScreenTests"`, `--filter-class "*WorkExecutorsTests"`.
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A PanoramicData.NugetManagement.Web PanoramicData.NugetManagement.Test
git commit -m "feat: deliver issue fixes through the AI change pipeline and retire the oracle"
```

---

### Task 10: Register the services, update the spec, run the whole suite

**Files:**
- Modify: the Web project's service registration (find with `grep -rn "AddSingleton<WorkExecutors>\|AddScoped<WorkExecutors>\|WorkExecutors" PanoramicData.NugetManagement.Web/Program.cs`)
- Modify: `docs/superpowers/specs/2026-10-15-ai-change-pipeline-design.md`
- Modify: `PanoramicData.NugetManagement.Test/CoverageOptOutTests.cs` only if it fails on the new classes

**Interfaces:**
- Consumes: Tasks 1 to 9.
- Produces: a working app and a spec that matches what was built.

- [ ] **Step 1: Register what the executors now need**

Ensure `IGitHubWriteApi` (as `OctokitGitHubWriteApi`) and `LocalRepoService` are registered and resolvable by `WorkExecutors`. Because the Octokit client needs a token that is resolved at run time, follow how `CreateGitHubClientAsync` already builds the client in `WorkExecutors`, and build `new OctokitGitHubWriteApi(client)` the same way instead of injecting it, if the client is not a registered singleton.

- [ ] **Step 2: Add the grace period to the spec**

In the spec's "Decisions this spec assumes", add:

```
7. **No checks are believed only after a 2-minute grace.** GitHub registers check runs a moment after a
   push, so an empty list straight away is treated as "not yet", and only an empty list that persists
   past the grace period means the repository has no CI.
8. **Test files do not count against the path cap**, so a fix plus its reproducing test fits within it.
```

And in "The pipeline" step 7, change "No checks at all: merge on the local gate." to "No checks after the grace period: merge on the local gate."

- [ ] **Step 3: Build and run the whole suite**

Run: `dotnet build PanoramicData.NugetManagement.Test/PanoramicData.NugetManagement.Test.csproj` (0 warnings, 0 errors), then in the background (it can exceed ten minutes): `./PanoramicData.NugetManagement.Test/bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe --filter-not-trait "Category=Integration"`.
Expected: all tests pass. The known expected failure from a tool call is `DetachedProcessLaunchTests.Start_FromInsideAJobObject_LaunchesTheChildOutsideIt`. Any other failure is a regression in this branch: read the output, do not wave it through.

- [ ] **Step 4: Check the app starts the pipeline for real, on a throwaway repository**

Using a scratch GitHub repository (not an estate repository) with a small test project and one known failing behaviour, run Fix with AI on it and confirm: a branch `ai/...` appears, a PR opens with the reproduction in its body, CI runs, and the app merges it. This is the only check that exercises GitHub; the unit tests use fakes. Report the result as it is, including anything that did not work.

- [ ] **Step 5: Commit and merge**

```bash
git add -A
git commit -m "feat: register the AI change pipeline and update its spec"
git fetch origin main
git rebase origin/main
```

Rebuild and rerun the new test classes after the rebase, then push the branch and open a pull request for review rather than pushing to `main`, because this changes how AI fixes reach every repository.
