using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Web.Services;

public sealed partial class WorkExecutors
{
	/// <summary>
	/// The line coverage a TST-10 session aims for locally, a little above Codacy's 60% so that a small
	/// difference in what each side counts does not leave a "finished" session short of the real bar.
	/// </summary>
	private const double _coverageTarget = 62;

	/// <summary>Turns a coverage session gets per attempt: reading, writing and measuring is many steps.</summary>
	private const int _coverageTurnsPerAttempt = 30;

	/// <summary>How many points of coverage a failed session must have added for its tests to be kept.</summary>
	private const double _coverageProgressWorthKeeping = 2;

	/// <summary>
	/// How long any other AI session may run in all, checks included. The one that prompted this ran thirty-four
	/// minutes on a change the model made in two, nearly all of it waiting for tests that call a live service.
	/// </summary>
	private static readonly TimeSpan _sessionTimeBudget = TimeSpan.FromMinutes(20);

	/// <summary>How long a coverage session may run in all, whatever its turns and attempts allow.</summary>
	private static readonly TimeSpan _coverageTimeBudget = TimeSpan.FromMinutes(25);

	/// <summary>
	/// Builds the test project a just-written file belongs to, and says whether it compiled.
	/// </summary>
	/// <returns>Null for anything that is not C# or belongs to no test project.</returns>
	private static async Task<string?> BuildFeedbackAsync(
		string cloneRoot,
		IReadOnlyList<string> testProjects,
		string writtenPath,
		CancellationToken cancellationToken)
	{
		if (!writtenPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var project = testProjects.FirstOrDefault(candidate =>
			writtenPath.StartsWith((Path.GetDirectoryName(candidate) ?? string.Empty).Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase));

		if (project is null)
		{
			return null;
		}

		var (ok, text) = await LocalRepoService
			.RunCommandWithStreamingAsync(cloneRoot, "dotnet", ["build", project, "--nologo", "--verbosity", "quiet"], null, cancellationToken, null)
			.ConfigureAwait(false);

		if (ok)
		{
			return "It compiles.";
		}

		var errors = text.Split('\n')
			.Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
			.Select(line => line.Trim())
			.Distinct()
			.Take(12);

		return "IT DOES NOT COMPILE. Fix this before writing anything else:\n" + string.Join("\n", errors);
	}

	/// <summary>A coverage run as one phrase for the status line, for example "coverage 54.5% of 62%".</summary>
	private static string CoverageStatus(CoverageRun run)
		=> run.Report is null
			? "coverage not measurable"
			: $"coverage {run.Report.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}% of "
				+ $"{_coverageTarget.ToString("0", CultureInfo.InvariantCulture)}%";

	/// <summary>
	/// A coverage run as the model reads it: whether the tests passed, the figure, and where to look next.
	/// </summary>
	private static string DescribeCoverage(CoverageRun run, IReadOnlySet<string> knownFailures)
	{
		if (run.Report is null)
		{
			return $"Coverage could not be measured. {run.Problem}\n{run.OutputTail}".TrimEnd();
		}

		string tests;

		if (run.TestsPassed)
		{
			tests = "All tests passed.";
		}
		else if (OnlyKnownFailures(run, knownFailures))
		{
			// Said plainly, because a model shown a red test run will try to turn it green. These failed
			// before it wrote anything, for want of a credential or a service CI provides and this
			// sandbox deliberately does not — they are not its to fix, and not its to edit.
			tests = $"{run.FailedTests!.Count} test(s) fail, and every one of them failed before you started "
				+ "(they need a credential or a live service). Ignore them. Do not edit them.";
		}
		else
		{
			var added = (run.FailedTests ?? new HashSet<string>()).Where(name => !knownFailures.Contains(name)).Order().ToList();

			tests = (added.Count > 0
				? $"Tests that passed before now FAIL or are new and failing: {string.Join(", ", added)}. Fix those first.\n"
				: "The test run failed. Fix that first.\n")
				+ run.OutputTail;
		}

		return $"{tests}\n{run.Report.Describe(_coverageTarget)}";
	}

	/// <summary>
	/// Whether a failing test run failed only in tests that were already failing before the session.
	/// </summary>
	private static bool OnlyKnownFailures(CoverageRun run, IReadOnlySet<string> knownFailures)
		=> !run.TestsPassed
			&& run.Report is not null
			&& run.FailedTests is { Count: > 0 } failed
			&& failed.All(knownFailures.Contains);

	/// <summary>
	/// The success criterion for a TST-10 session: the tests pass, and the figure they produce is at the
	/// target.
	/// </summary>
	/// <remarks>
	/// A real measurement, so it can be failed with real information: how far short, and in which files,
	/// which is what the next attempt needs and what a rule that only says "no figure" never could.
	/// </remarks>
	private async Task<AiRuleCheck> CheckCoverageAsync(
		RepositoryDashboardRow row,
		AiFixToolbox toolbox,
		IReadOnlySet<string> knownFailures,
		Action<string> noteCoverage,
		CancellationToken cancellationToken)
	{
		if (toolbox.FilesWritten.Count == 0)
		{
			return new AiRuleCheck(false, "You have not written any test yet, so coverage has not changed.");
		}

		var run = await CoverageMeasurement.MeasureAsync(row.LocalPath!, null, cancellationToken).ConfigureAwait(false);
		noteCoverage(CoverageStatus(run));
		var testsFine = run.TestsPassed || OnlyKnownFailures(run, knownFailures);
		var passed = run.Report is not null && testsFine && run.Report.LinePercent >= _coverageTarget;

		Say(DescribeMeasurement(run, testsFine));

		return new AiRuleCheck(
			passed,
			passed
				? $"Local line coverage is {run.Report!.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}% and no test you could have affected fails."
				: DescribeCoverage(run, knownFailures));
	}

	internal static string DescribeMeasurement(CoverageRun run, bool testsFine)
	{
		if (run.Report is null)
		{
			return $"   ⚠️ Coverage could not be measured: {run.Problem}";
		}

		var tests = run.TestsPassed ? "tests pass." : testsFine ? "only already-failing tests fail." : "tests FAIL.";
		return $"   📏 {run.Report.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}% line coverage, {tests}";
	}

	/// <summary>
	/// How many diagnostic lines of a build or test run the model is shown.
	/// </summary>
	/// <remarks>
	/// Enough to fix what it broke, few enough to leave room for the task. A model that has caused
	/// forty errors has caused one error forty times.
	/// </remarks>
	private const int _diagnosticLineLimit = 30;

	/// <summary>
	/// Notes which files are already changed in the clone, and what they contain, so the Changes tab can
	/// label them pre-existing. Runs git off the calling thread. A baseline is only a label, so any
	/// failure leaves it empty rather than failing or delaying the fix.
	/// </summary>
	internal static async Task RecordPreExistingChangesAsync(WorkItem item, string clonePath, CancellationToken cancellationToken = default)
	{
		try
		{
			item.PreExistingChanges = await Task
				.Run(() => new CloneChangeReader().Baseline(clonePath), cancellationToken)
				.WaitAsync(cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// A cancelled fix stops now rather than after the baseline.
			throw;
		}
		catch (Exception)
		{
			item.PreExistingChanges = new Dictionary<string, string>(StringComparer.Ordinal);
		}
	}

	/// <summary>
	/// What a build or test run says, in a form worth sending to a model and worth keeping in a
	/// transcript.
	/// </summary>
	/// <remarks>
	/// Two problems, one cause. The run's output used to go straight to <see cref="Say"/>, and
	/// <c>dotnet build --verbosity normal</c> prints every compiler command line it issues — thousands
	/// of lines of reference paths. A transcript holds five hundred lines, so one build erased the
	/// whole session from the pane it was meant to be read in.
	/// <para>
	/// Meanwhile the model was handed <c>row.StatusMessage</c>, which is the words "Build failed." and
	/// nothing else — so a model that had just broken the build was told only that it had, and its next
	/// turn was a guess. The errors are the one part of that output it can act on, so the errors are
	/// what it gets.
	/// </para>
	/// </remarks>
	private static string SummariseRun(string what, bool succeeded, List<string> output)
	{
		var verdict = succeeded ? $"{what} succeeded." : $"{what} failed.";

		if (succeeded)
		{
			return verdict;
		}

		var diagnostics = output
			.Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase)
				|| (line.Contains("failed:", StringComparison.OrdinalIgnoreCase)
					// A summary line reading "failed: 0" is the opposite of a diagnostic, and listing it
					// under "Tests failed." told a model the run had failed for no reason it could find.
					&& !line.Contains("failed: 0", StringComparison.OrdinalIgnoreCase))
				|| line.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.Ordinal)
			.Take(_diagnosticLineLimit)
			.ToList();

		if (diagnostics.Count > 0)
		{
			return $"{verdict}\n{string.Join("\n", diagnostics)}";
		}

		// Nothing recognisable as an error: no test failed, so the run stopped for another reason — a build
		// that broke before any test started, a crash, no tests found. The end of the output is where
		// that reason is, and it is better to show it than to say there is nothing to show.
		var tail = output
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.TakeLast(_diagnosticLineLimit / 2)
			.ToList();

		return tail.Count == 0
			? $"{verdict} The run produced no output."
			: $"{verdict} No individual error was recognised. The end of the output was:\n{string.Join("\n", tail)}";
	}

	/// <summary>
	/// Whether the rule's verdict comes from a service reading the published branch.
	/// </summary>
	private static bool IsRemotelyGraded(string ruleId)
		=> PanoramicData.NugetManagement.Services.RuleRegistry.Rules
			.First(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
			is IRemotelyGraded;

	/// <summary>
	/// The success criterion for a rule that cannot be asked about the working tree: something was
	/// written, and the repository still builds.
	/// </summary>
	/// <remarks>
	/// A weaker claim than "the rule passes", and honestly so — nothing here can know whether Codacy
	/// will like the change. It is, though, the whole of what is knowable locally, and it separates the
	/// two failures that matter: a model that narrated for twelve turns and changed nothing, and one
	/// that changed something and broke the build. Both are caught; a plausible edit is kept and left
	/// for a person to read.
	/// <para>
	/// Tests are not run. A repository whose tests take ten minutes would spend them on every file of
	/// every Codacy fix, and a change that compiles is the bar this criterion is honest about clearing.
	/// </para>
	/// </remarks>
	/// <summary>
	/// The test classes that can have changed behaviour, when every file written is C# inside a test
	/// project; otherwise null, meaning run everything.
	/// </summary>
	/// <param name="cloneRoot">The repository clone.</param>
	/// <param name="written">The files written, relative to the clone.</param>
	internal static IReadOnlyList<string>? TestClassesAffectedBy(string cloneRoot, IReadOnlyCollection<string> written)
	{
		if (written.Count == 0 || written.Any(file => !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
		{
			return null;
		}

		var folders = CoverageMeasurement.FindTestProjects(cloneRoot)
			.Select(project => (Path.GetDirectoryName(project) ?? string.Empty).Replace('\\', '/'))
			.Where(folder => folder.Length > 0)
			.ToList();

		if (written.Any(file => !folders.Any(folder => file.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))))
		{
			return null;
		}

		var classes = written
			.Select(file => Path.Combine(cloneRoot, file))
			.Where(File.Exists)
			.SelectMany(path => CoverageMeasurement.ClassesDeclaredIn(File.ReadAllText(path)))
			.Distinct(StringComparer.Ordinal)
			.ToList();

		return classes.Count == 0 ? null : classes;
	}

	private async Task<AiRuleCheck> CheckLocalChangeAsync(
		RepositoryDashboardRow row,
		AiFixToolbox toolbox,
		IReadOnlySet<string>? testBaseline,
		CancellationToken cancellationToken,
		(DuplicationReport Before, string Target)? duplication = null)
	{
		if (toolbox.FilesWritten.Count == 0)
		{
			return new AiRuleCheck(false, "Nothing was written, so nothing changed.");
		}

		// Captured rather than said, for the reason SummariseRun gives: a verbose build streamed into
		// the transcript pushes the session out of it.
		var captured = new List<string>();
		await dashboard.BuildAsync(row, captured.Add, cancellationToken, restore: true).ConfigureAwait(false);

		var built = row.Status == PackageStatus.BuildSucceeded;

		RememberBuildResult(row, built ? RepositoryBuildState.Succeeded : RepositoryBuildState.Failed);

		var summary = SummariseRun("Build", built, captured);
		Say(built ? $"   ✓ {summary}" : $"   ⚠️ {summary}");

		if (!built)
		{
			return new AiRuleCheck(false, summary);
		}

		if (testBaseline is not null && await CheckTestsAgainstBaselineAsync(row, toolbox, testBaseline, cancellationToken).ConfigureAwait(false) is { } testFailure)
		{
			return testFailure;
		}

		if (duplication is { } measure && await CheckDuplicationAsync(row, measure.Before, measure.Target, cancellationToken).ConfigureAwait(false) is { } duplicationFailure)
		{
			return duplicationFailure;
		}

		return new AiRuleCheck(true, $"{toolbox.FilesWritten.Count} file(s) changed and the build succeeded.");
	}

	/// <summary>Runs the tests and returns a failing check when any test that passed before now fails, otherwise null.</summary>
	private async Task<AiRuleCheck?> CheckTestsAgainstBaselineAsync(
		RepositoryDashboardRow row,
		AiFixToolbox toolbox,
		IReadOnlySet<string> testBaseline,
		CancellationToken cancellationToken)
	{
		// A change that compiles can still break behaviour: refactoring duplicated tests to satisfy Codacy
		// once left a test failing, and a build-only check pushed it. So the tests run too, in the sandbox,
		// and only a test that passed before and fails now counts against the session — the ones already
		// failing here need a credential CI has and this run withholds.
		var testOutput = new List<string>();

		// When the session touched nothing but test source, only the classes in those files can have
		// changed behaviour, so only they are run. In a library whose tests call a live service the
		// whole suite takes five to twenty-five minutes and fails on its own account now and then; one
		// session was told that tests in an unrelated class had broken, and spent its last attempt on it.
		var scope = TestClassesAffectedBy(row.LocalPath!, toolbox.FilesWritten);

		if (scope is not null)
		{
			Say($"   🧪 Running only the classes in the changed test file(s): {string.Join(", ", scope)}.");
		}

		await dashboard.RunTestsAsync(row, testOutput.Add, cancellationToken, sandbox: true, testClasses: scope).ConfigureAwait(false);

		var broken = CoverageMeasurement.ParseFailedTests(string.Join('\n', testOutput))
			.Where(name => !testBaseline.Contains(name))
			.Order(StringComparer.Ordinal)
			.ToList();

		if (broken.Count > 0)
		{
			Say($"   ⚠️ {broken.Count.ToString(CultureInfo.InvariantCulture)} test(s) that passed before now fail.");

			return new AiRuleCheck(
				false,
				"Your change builds, but these tests passed before you started and fail now: "
				+ $"{string.Join(", ", broken.Take(10))}. Fix them without undoing what you changed.\n"
				+ SummariseRun("Tests", false, testOutput));
		}

		Say("   ✓ No test that passed before fails now.");
		return null;
	}

	/// <summary>Measures repeated code again and returns a failing check when it has not meaningfully dropped, otherwise null.</summary>
	private async Task<AiRuleCheck?> CheckDuplicationAsync(
		RepositoryDashboardRow row,
		DuplicationReport before,
		string target,
		CancellationToken cancellationToken)
	{
		// Last, because it is the cheap check and the others are the ones that can fail for reasons the model can
		// fix quickly; but it is the one that says whether the change did anything about the grade.
		var after = await Task.Run(() => DuplicationMeasurement.Measure(row.LocalPath!, target, DuplicationMeasurement.DefaultMinTokens), cancellationToken)
			.ConfigureAwait(false);

		if (after is null)
		{
			return null;
		}

		Say($"   📏 Repeated code in {target}: "
			+ $"{before.Percent.ToString("0", CultureInfo.InvariantCulture)}% → "
			+ $"{after.Percent.ToString("0", CultureInfo.InvariantCulture)}%.");

		return DuplicationMeasurement.IsMeaningfulImprovement(before, after)
			? null
			: new AiRuleCheck(
				false,
				$"The build passes, but {target} is still {after.Percent.ToString("0", CultureInfo.InvariantCulture)}% "
				+ $"repeated (it was {before.Percent.ToString("0", CultureInfo.InvariantCulture)}%). "
				+ "Reformatting or joining lines does not change that: it is the same code in more than one place. "
				+ "Put a repeated run in one shared place — a base interface, a helper method, a new file in the "
				+ "same folder — and use it from each copy.\n"
				+ DuplicationMeasurement.Describe(after, 8));
	}

	/// <summary>
	/// The rule's verdict, and — when the rule is satisfied by something that can break compilation —
	/// whether the repository still builds.
	/// </summary>
	/// <remarks>
	/// A rule that reads project or package files passes the moment the text is right, whatever that did
	/// to the code. The first session to exercise this swapped a package reference, satisfied PKG-12, and
	/// left the test project unable to compile; the next session's build check then failed for a reason
	/// it had no part in. Making the build part of the answer sends that failure back to the session
	/// that caused it, with the compiler's errors, while it is still in the room.
	/// </remarks>
	private async Task<AiRuleCheck> ReEvaluateAndBuildAsync(
		RepositoryDashboardRow row,
		string ruleId,
		AiFixToolbox toolbox,
		bool buildWasBroken,
		CancellationToken cancellationToken)
	{
		var check = await ReEvaluateAsync(row, ruleId, cancellationToken).ConfigureAwait(false);

		if (!check.Passed || buildWasBroken || toolbox.FilesWritten.Count == 0)
		{
			return check;
		}

		var captured = new List<string>();
		await dashboard.BuildAsync(row, captured.Add, cancellationToken, restore: true).ConfigureAwait(false);

		var built = row.Status == PackageStatus.BuildSucceeded;

		RememberBuildResult(row, built ? RepositoryBuildState.Succeeded : RepositoryBuildState.Failed);

		if (built)
		{
			return check;
		}

		var summary = SummariseRun("Build", false, captured);
		Say($"   ⚠️ {summary}");

		return new AiRuleCheck(
			false,
			"The rule is now satisfied, but your change stopped the repository building. Fix the build "
			+ $"without undoing what satisfied the rule.\n{summary}");
	}

	/// <summary>
	/// Evaluates one rule against the repository's clone as it now stands.
	/// </summary>
	/// <remarks>
	/// A fresh context each time. A cached one would report the repository as it was before the model
	/// edited it, so every attempt would look like a failure and the loop would exhaust its retries
	/// against its own stale reading.
	/// </remarks>
	private async Task<AiRuleCheck> ReEvaluateAsync(
		RepositoryDashboardRow row,
		string ruleId,
		CancellationToken cancellationToken)
	{
		var result = await EvaluateRuleAsync(row, ruleId, cancellationToken).ConfigureAwait(false);

		return new AiRuleCheck(result.Passed, result.Message);
	}

	/// <summary>
	/// Evaluates one rule against the repository as it now stands and returns everything it said, advisory included.
	/// </summary>
	private async Task<PanoramicData.NugetManagement.Models.RuleResult> EvaluateRuleAsync(
		RepositoryDashboardRow row,
		string ruleId,
		CancellationToken cancellationToken)
	{
		var github = await CreateGitHubClientAsync().ConfigureAwait(false);
		var context = await dashboard.BuildContextAsync(row, github, cancellationToken).ConfigureAwait(false);

		var rule = PanoramicData.NugetManagement.Services.RuleRegistry.Rules
			.First(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

		return await rule.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Swaps one rule's result in the cached assessment, so the tree reflects the fix without a full
	/// re-assessment.
	/// </summary>
	private static void ReplaceRuleResult(RepositoryDashboardRow row, string ruleId, AiRuleCheck check)
	{
		if (row.Assessment is null)
		{
			return;
		}

		var index = row.Assessment.RuleResults.FindIndex(r =>
			string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

		if (index < 0)
		{
			return;
		}

		var existing = row.Assessment.RuleResults[index];

		row.Assessment.RuleResults[index] = new PanoramicData.NugetManagement.Models.RuleResult
		{
			RuleId = existing.RuleId,
			RuleName = existing.RuleName,
			Category = existing.Category,
			Severity = existing.Severity,
			IsApplicable = existing.IsApplicable,
			Passed = check.Passed,
			Message = check.Message,
			Advisory = check.Passed ? null : existing.Advisory
		};
	}

	private static RuleResult? FindRule(RepositoryDashboardRow row, string ruleId)
		=> row.Assessment?.RuleResults
			.FirstOrDefault(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

	internal static string DescribeFix(string ruleId, int appliedCount)
		=> appliedCount > 0 ? $"✅ Fixed {ruleId}" : $"⚠️ Could not fix {ruleId}";

	/// <summary>
	/// Applies the auto-remediation of one rule to a repository, then re-assesses it.
	/// </summary>
	/// <remarks>
	/// The rule is looked up on the row's current assessment rather than trusted from when it was
	/// queued: the queue can take minutes to reach an item, by which time the rule may already have
	/// been fixed by something else, in which case the item is skipped rather than re-applied.
	/// </remarks>
	/// <param name="item">The item naming the repository and, via <c>ruleId</c>, which rule to fix.</param>
	/// <param name="progress">Unused: a single-rule fix reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task FixRuleAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var ruleId = item.Descriptor.Parameter("ruleId")!;
		var result = FindRule(row, ruleId);

		if (result is null || result.Passed)
		{
			logger.LogWarning(
				"Skipping {RuleId} on {Repository}: it is no longer failing.",
				ruleId,
				row.RepositoryFullName);
			return;
		}

		if (row.LocalPath is null)
		{
			Say($"⏭️ {row.RepositoryFullName} is no longer cloned locally — skipping {result.RuleId}.");
			return;
		}

		Say($"▶ Fixing {result.RuleId}...");

		// Whether the write phase finished. A single remediation is applied synchronously and cannot
		// be interrupted part-way, so in this executor the flag is only ever false for a Stop observed
		// before the call — but it is tracked the same way as its two siblings so the three do not
		// diverge in behaviour that is this easy to get wrong.
		var remediationCompleted = false;

		try
		{
			var applied = await dashboard
				.ApplyRuleRemediationAsync(row, result, Say, cancellationToken)
				.ConfigureAwait(false);
			remediationCompleted = true;
			governance?.RecordRemediation(item, row.RepositoryFullName, result.RuleId, applied);

			await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
			cache.UpsertRow(row);
			Say(DescribeFix(result.RuleId, applied.Count));

			await ReassessAfterFixAsync(row, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Atomic over the WRITE phase only. A remediation stopped part-way is undone, so the clone
			// is left as it was found rather than carrying half a fix into the next commit — but once
			// the remediation has landed, the re-assessment that follows only reads the clone, and
			// throwing away a fix the user asked for because a subsequent read was interrupted would
			// destroy work they never asked to undo.
			await RevertIfRemediationIncompleteAsync(row, remediationCompleted).ConfigureAwait(false);
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Undoes a stopped fix, but only when the remediation itself did not finish.
	/// </summary>
	/// <remarks>
	/// The atomicity a stopped fix owes the user is over the write phase, not over the whole executor.
	/// Remediation writes to the clone; the git-status refresh and the re-assessment that follow it
	/// only read. Reverting after the remediation has landed would discard a fix the user explicitly
	/// asked for because a subsequent read was interrupted — destroying work they never asked to undo,
	/// on a path they reached by pressing Stop to save time.
	/// <para>
	/// The stop is still reported either way, so the console never goes quiet on a Stop; only the
	/// discard is conditional.
	/// </para>
	/// </remarks>
	/// <param name="row">The repository that was being fixed.</param>
	/// <param name="remediationCompleted">
	/// Whether the write phase ran to completion. False means the clone may hold a partly-written fix.
	/// </param>
	private async Task RevertIfRemediationIncompleteAsync(RepositoryDashboardRow row, bool remediationCompleted)
	{
		if (remediationCompleted)
		{
			Say("⏹️ Stopped after the remediation had been applied — the applied changes are kept.");
			return;
		}

		await RevertPartAppliedFixAsync(row).ConfigureAwait(false);
	}
}
