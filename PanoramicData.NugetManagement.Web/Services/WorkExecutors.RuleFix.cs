using System.Globalization;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Web.Services;

public sealed partial class WorkExecutors
{
	/// <summary>
	/// Has the local model fix one rule that no deterministic remediation covers.
	/// </summary>
	/// <remarks>
	/// Runs on the repository's own lane rather than a lane of its own, so that nothing else can be
	/// writing to the same working tree while the model is. What bounds the load on the model's server is
	/// <see cref="OllamaGate"/>, which is a separate concern from keeping one clone to one writer.
	/// <para>
	/// On failure the clone is reverted. Half-finished edits from a model that misunderstood the task are
	/// worse than no attempt: somebody has to unpick them, and the transcript in this item's output says
	/// what was tried either way.
	/// </para>
	/// </remarks>
	/// <param name="item">The item naming the repository and the rule.</param>
	/// <param name="progress">Unused: the transcript goes to the console.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task FixWithAiRuleAsync(
		WorkItem item,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		if (await PrepareRuleFixAsync(item, progress, cancellationToken).ConfigureAwait(false) is not { } fix)
		{
			return;
		}

		try
		{
			await RunRuleFixSessionAsync(fix, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			Say($"⏹️ Stopped {fix.RuleId} on {fix.Row.RepositoryFullName}.");
			throw;
		}
		finally
		{
			await FinishRuleFixAsync(fix, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Everything one AI rule fix knows and gathers as it goes, so its phases can be separate methods.</summary>
	private sealed class RuleFixContext(WorkItem item, IProgress<string> progress, RepositoryDashboardRow row, string ruleId, string? targetPath, OllamaOptions ollama, RuleResult result)
	{
		public WorkItem Item { get; } = item;

		public IProgress<string> Progress { get; } = progress;

		public RepositoryDashboardRow Row { get; } = row;

		public string RuleId { get; } = ruleId;

		public string? TargetPath { get; } = targetPath;

		public OllamaOptions Ollama { get; } = ollama;

		public RuleResult Result { get; } = result;

		public bool Succeeded { get; set; }

		public Func<int>? UndoWrites { get; set; }

		public Func<Task<string?>>? ProgressWorthKeeping { get; set; }

		// What the governance ledger needs once the session is over, gathered as it goes because the
		// session's own variables are out of scope by the time it is recorded.
		public AiFixRequest? RecordedRequest { get; set; }

		public AiFixOutcome? RecordedOutcome { get; set; }

		public AiFixToolbox? Toolbox { get; set; }

		public string? RecordedLogPath { get; set; }

		public bool CoverageMode { get; set; }

		public CoverageRun? Baseline { get; set; }

		// The latest coverage figure, shown in the pane's status line next to the turn and the clock.
		public string? CoverageNow { get; set; }

		public IReadOnlyList<string> TestFolders { get; set; } = [];

		public IReadOnlyList<string> TestProjects { get; set; } = [];

		public IReadOnlySet<string> KnownFailures { get; set; } = new HashSet<string>();

		public DuplicationReport? DuplicationBefore { get; set; }

		public bool RemotelyGraded { get; set; }

		public bool BuildWasBroken { get; set; }

		public bool VerifyTests { get; set; }

		public IReadOnlySet<string> TestBaseline { get; set; } = new HashSet<string>();

		public IRuleAiPlaybook? Playbook { get; set; }
	}

	/// <summary>Checks that the fix can go ahead and gathers what it needs, saying why and returning null when it cannot.</summary>
	private async Task<RuleFixContext?> PrepareRuleFixAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);

		if (row is null)
		{
			SayRepositoryGone(item);
			return null;
		}

		var ruleId = item.Descriptor.Parameter("ruleId")!;
		var targetPath = item.Descriptor.Parameter("path");
		var ollama = runtimeSettings.Ollama;

		if (UnstartableReason(row, ollama, ruleId, out var result) is { } reason)
		{
			Say(reason);
			return null;
		}

		if (await RefuseUndeliverableAsync(row, cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return await ConfirmStillFailingAsync(new RuleFixContext(item, progress, row, ruleId, targetPath, ollama, result), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Why the fix cannot start at all, or null; <paramref name="result"/> is the failing rule when it can.</summary>
	private string? UnstartableReason(RepositoryDashboardRow row, OllamaOptions ollama, string ruleId, out RuleResult result)
	{
		result = null!;

		if (!ollama.IsConfigured)
		{
			return "⚠️ No Ollama server is configured — set one under Settings, Ollama Config.";
		}

		if (row.LocalPath is null || !row.IsClonedLocally)
		{
			return $"⏭️ {row.RepositoryFullName} is not cloned locally, and the model edits files on disk.";
		}

		var found = FindRule(row, ruleId);

		if (found is null || found.Passed)
		{
			return $"⏭️ {ruleId} is no longer failing on {row.RepositoryFullName}.";
		}

		result = found;

		// Fix and Fix with AI are disjoint. A rule that gained a remediation since this item was queued
		// belongs to Fix now, and doing it here would spend a GPU on work a function can do exactly.
		return remediations.Get(ruleId) is not null
			? $"⏭️ {ruleId} now has an automatic remediation — use Fix instead."
			: null;
	}

	/// <summary>Says so and returns true when the clone holds changes the session did not make.</summary>
	private async Task<bool> RefuseUndeliverableAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		// A fix is delivered as a pull request, and delivery refuses a clone holding changes the session did not
		// make. So a session started on a dirty clone could only end with its work left uncommitted, and the next
		// one would find more of it: Aruba.Api and AlienFx.Api collected dozens of "successful" sessions that way
		// and never changed a grade. Said now, before minutes of model time are spent on a result that cannot go.
		await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);

		if (row.IsWorkingTreeClean != false)
		{
			return false;
		}

		Say($"⏭️ {row.RepositoryFullName} has uncommitted changes, so a fix made now could not be delivered. "
			+ "Press Fix with AI again: it first gets the clone out from behind them (builds and tests them, "
			+ "commits what passes, repairs or sets aside what does not), and this fix is queued behind that.");
		return true;
	}

	/// <summary>Re-reads a Codacy-graded rule and declines when Codacy's findings describe an older commit than the clone's.</summary>
	private async Task<RuleFixContext?> ConfirmStillFailingAsync(RuleFixContext fix, CancellationToken cancellationToken)
	{
		var row = fix.Row;
		var ruleId = fix.RuleId;
		var result = fix.Result;

		// Codacy's findings describe the commit it last analysed, and a clone that has moved on may already have
		// fixed them. A model given a finding that is already fixed changes something else instead: AlienFx.Api's
		// CQ-05 session renamed a parameter back to the name a merged commit had just replaced, and opened a pull
		// request that would have undone the fix. Declined here, before any model time is spent.
		var headSha = await localRepo.GetHeadShaAsync(row.RepositoryFullName, cancellationToken).ConfigureAwait(false);

		// The cached assessment may be hours old, or older than the freshness facts it would need to be judged by, so a
		// Codacy-graded rule is read again now. The session is then given what Codacy says today, and the gate below has
		// something to compare. TST-10 is excluded: it is measured locally and never describes a commit.
		if (IsRemotelyGraded(ruleId) && !string.Equals(ruleId, "TST-10", StringComparison.OrdinalIgnoreCase))
		{
			var current = await EvaluateRuleAsync(row, ruleId, cancellationToken).ConfigureAwait(false);

			if (current.Passed)
			{
				Say($"⏭️ {ruleId} is no longer failing on {row.RepositoryFullName}: Codacy has caught up with a fix.");
				return null;
			}

			result = current;
		}

		if (CodacyAnalysisGate.Refusal(result.Advisory?.Data, headSha) is { } notCurrent)
		{
			Say($"⏭️ {ruleId}: {notCurrent}");
			return null;
		}

		return new RuleFixContext(fix.Item, fix.Progress, row, ruleId, fix.TargetPath, fix.Ollama, result);
	}

	/// <summary>Runs the model against the rule and delivers the change when it succeeds.</summary>
	private async Task RunRuleFixSessionAsync(RuleFixContext fix, CancellationToken cancellationToken)
	{
		var row = fix.Row;

		// Queued behind whatever else wants the model. Entered before any file is touched, so a wait
		// for the server never leaves a half-edited clone lying around.
		Say($"⏳ Waiting for the model ({fix.Ollama.Model})...");
		using var hold = await ollamaGate.EnterAsync(cancellationToken).ConfigureAwait(false);

		using var client = CreateOllamaClient(fix.Ollama);

		// Before anything below can touch the clone: what is already changed is not this session's doing.
		await RecordPreExistingChangesAsync(fix.Item, row.LocalPath!, cancellationToken).ConfigureAwait(false);

		if (!await PrepareCoverageAsync(fix, cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		await PrepareDuplicationAsync(fix, cancellationToken).ConfigureAwait(false);

		fix.Toolbox = CreateRuleToolbox(fix);
		fix.UndoWrites = fix.Toolbox.Undo;
		fix.ProgressWorthKeeping = CreateProgressCheck(fix);

		await PrepareTestBaselineAsync(fix, cancellationToken).ConfigureAwait(false);

		var outcome = await RunModelAsync(fix, client, cancellationToken).ConfigureAwait(false);
		fix.Succeeded = outcome.Succeeded;
		fix.RecordedOutcome = outcome;

		await ReportOutcomeAsync(fix, outcome, hold, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// TST-10 is the one remotely graded rule that can be measured locally: the same tests, run
	/// under coverage, give a figure close to the one Codacy will later report. Measured before the
	/// model starts, so it is told where it stands and a repository that cannot be measured is
	/// declined now rather than after twelve turns of writing tests nothing can score.
	/// </summary>
	/// <returns>False when the session must not start.</returns>
	private async Task<bool> PrepareCoverageAsync(RuleFixContext fix, CancellationToken cancellationToken)
	{
		var row = fix.Row;
		fix.CoverageMode = string.Equals(fix.RuleId, "TST-10", StringComparison.OrdinalIgnoreCase);
		fix.KnownFailures = new HashSet<string>();

		if (!fix.CoverageMode)
		{
			return true;
		}

		fix.TestProjects = CoverageMeasurement.FindTestProjects(row.LocalPath!);

		fix.TestFolders =
		[
			.. fix.TestProjects
				.Select(project => (Path.GetDirectoryName(project) ?? string.Empty).Replace('\\', '/'))
				.Where(folder => folder.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
		];

		if (fix.TestFolders.Count == 0)
		{
			Say($"⏭️ {row.RepositoryFullName} has no test project to add tests to, so coverage cannot be raised here.");
			return false;
		}

		Say("📏 Measuring coverage before the model starts...");
		fix.Baseline = await CoverageMeasurement.MeasureAsync(row.LocalPath!, null, cancellationToken).ConfigureAwait(false);

		if (fix.Baseline.Report is null)
		{
			Say($"⏭️ Coverage cannot be measured locally: {fix.Baseline.Problem}");
			return false;
		}

		fix.CoverageNow = CoverageStatus(fix.Baseline);

		Say($"📏 {fix.Baseline.Report.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}% to start, aiming for "
			+ $"{_coverageTarget.ToString("0", CultureInfo.InvariantCulture)}%.");

		// What already fails here, before the model has touched anything. CI gives these tests the
		// credentials this sandbox withholds, so they fail in both worlds for a reason that has nothing
		// to do with coverage — and are told to the model as known, not left for it to discover.
		fix.KnownFailures = fix.Baseline.FailedTests ?? new HashSet<string>();
		return true;
	}

	/// <summary>
	/// A file graded down for duplication is repeated across files, so editing it alone cannot fix it, and
	/// the only local check — does it build — passes for a change that joins two lines. Measured here
	/// instead, so the session is told where the repeats are, may touch the files that share them, and is
	/// judged on whether the file really became less repetitive. Aruba.Api's interfaces were "fixed" three
	/// and four times each by reformatting, and graded F every time.
	/// </summary>
	private async Task PrepareDuplicationAsync(RuleFixContext fix, CancellationToken cancellationToken)
	{
		var targetPath = fix.TargetPath;

		if (!string.Equals(fix.RuleId, "CQ-06", StringComparison.OrdinalIgnoreCase) || targetPath is not { Length: > 0 })
		{
			return;
		}

		var localPath = fix.Row.LocalPath!;
		var measured = await Task.Run(() => DuplicationMeasurement.Measure(localPath, targetPath, DuplicationMeasurement.DefaultMinTokens), cancellationToken)
			.ConfigureAwait(false);

		// Below this the grade is about something else — complexity, an issue — and a duplication check
		// would reject a change that was right.
		if (measured is { Percent: >= 15 })
		{
			fix.DuplicationBefore = measured;
			Say($"📏 {targetPath} is {measured.Percent.ToString("0", CultureInfo.InvariantCulture)}% repeated by a local "
				+ $"measure, across {measured.PeerFiles.Count.ToString(CultureInfo.InvariantCulture)} other file(s).");
		}
	}

	private AiFixToolbox CreateRuleToolbox(RuleFixContext fix)
	{
		var row = fix.Row;
		var coverageMode = fix.CoverageMode;

		return new AiFixToolbox(
			row.LocalPath!,
			build: BuildTool(row),
			test: TestTool(row),
			// Confined to the test project, so "raising coverage" cannot be done by deleting the code
			// that is uncovered or by editing it until it is easy to cover.
			writableFolders: WritableFolders(fix),

			// The file and the files that share its repeats, and nothing else: moving a repeat into one place
			// means changing every copy.
			writablePaths: fix.DuplicationBefore is { } duplication ? [fix.TargetPath!, .. duplication.PeerFiles] : null,
			coverage: coverageMode ? token => MeasureCoverageAsync(fix, token) : null,

			// Only new files: "add tests" is not a licence to change the ones that exist.
			newFilesOnly: coverageMode || fix.DuplicationBefore is not null,

			// Whether the test project still compiles, said in the result of the write that broke it.
			afterWrite: coverageMode
				? (path, token) => BuildFeedbackAsync(row.LocalPath!, fix.TestProjects, path, token)
				: null,

			// Not offered where the check already runs the tests for the model: in a repository whose tests call
			// a live service each run is minutes, and one session spent twenty-four of them on six calls that
			// could not change anything it was able to do.
			offerRunTests: !(IsRemotelyGraded(fix.RuleId) && !coverageMode));
	}

	private static IReadOnlyList<string>? WritableFolders(RuleFixContext fix)
	{
		if (fix.CoverageMode)
		{
			return fix.TestFolders;
		}

		var targetFolder = fix.DuplicationBefore is not null ? Path.GetDirectoryName(fix.TargetPath)?.Replace('\\', '/') : null;
		return targetFolder is { Length: > 0 } ? [targetFolder] : null;
	}

	private static async Task<string> MeasureCoverageAsync(RuleFixContext fix, CancellationToken token)
	{
		var run = await CoverageMeasurement.MeasureAsync(fix.Row.LocalPath!, null, token).ConfigureAwait(false);
		fix.CoverageNow = CoverageStatus(run);

		return DescribeCoverage(run, fix.KnownFailures);
	}

	/// <summary>
	/// A coverage session that falls short has usually still done most of the work, and throwing it
	/// all away makes the next attempt start from nothing. If what is left compiles, passes, and has
	/// moved the figure, it is progress, and keeping it is what lets the estate climb in steps.
	/// </summary>
	private Func<Task<string?>>? CreateProgressCheck(RuleFixContext fix)
	{
		if (!fix.CoverageMode || fix.Baseline?.Report is not { } startingPoint)
		{
			return null;
		}

		return () => MeasureKeptProgressAsync(fix, startingPoint);
	}

	private static async Task<string?> MeasureKeptProgressAsync(RuleFixContext fix, CoberturaReport startingPoint)
	{
		if (fix.Toolbox!.FilesWritten.Count == 0)
		{
			return null;
		}

		var final = await CoverageMeasurement.MeasureAsync(fix.Row.LocalPath!, null, CancellationToken.None).ConfigureAwait(false);
		return DescribeKeptProgress(startingPoint, final, fix.KnownFailures);
	}

	internal static string? DescribeKeptProgress(CoberturaReport startingPoint, CoverageRun final, IReadOnlySet<string> knownFailures)
	{
		if (final.Report is not { } reached
			|| !(final.TestsPassed || OnlyKnownFailures(final, knownFailures))
			|| reached.LinePercent < startingPoint.LinePercent + _coverageProgressWorthKeeping)
		{
			return null;
		}

		return $"{startingPoint.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}% to {reached.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}%";
	}

	/// <summary>
	/// Notes which tests already fail, before the model changes anything, so a later failure can be told
	/// apart from one it caused. Only worth the time where the check will use it: a remote rule that is
	/// not the coverage one, in a repository that builds.
	/// </summary>
	private async Task PrepareTestBaselineAsync(RuleFixContext fix, CancellationToken cancellationToken)
	{
		var row = fix.Row;

		// Noted before the model touches anything. A repository that already fails to build cannot be
		// held to a build it never had, and would trap the session in a correction it cannot satisfy.
		fix.BuildWasBroken = row.LastBuildState == RepositoryBuildState.Failed;
		fix.Playbook = playbooks.For(fix.RuleId);

		// A rule graded on the published branch cannot answer whether this clone improved, so the
		// loop is told to stop asking it. Retrying is pointless for the same reason: the correction
		// fed back would be the same sentence every time.
		fix.RemotelyGraded = IsRemotelyGraded(fix.RuleId);
		fix.VerifyTests = fix.RemotelyGraded && !fix.CoverageMode && !fix.BuildWasBroken;

		if (!fix.VerifyTests)
		{
			return;
		}

		Say("🧪 Noting which tests already fail here, before the model starts...");

		var buildOutput = new List<string>();
		await dashboard.BuildAsync(row, buildOutput.Add, cancellationToken, restore: true).ConfigureAwait(false);

		if (row.Status != PackageStatus.BuildSucceeded)
		{
			fix.VerifyTests = false;
			return;
		}

		var testOutput = new List<string>();
		await dashboard.RunTestsAsync(row, testOutput.Add, cancellationToken, sandbox: true, null).ConfigureAwait(false);
		fix.TestBaseline = CoverageMeasurement.ParseFailedTests(string.Join('\n', testOutput));
	}

	private async Task<AiFixOutcome> RunModelAsync(RuleFixContext fix, Ollama.Api.OllamaClient client, CancellationToken cancellationToken)
	{
		var row = fix.Row;
		var ollama = fix.Ollama;
		var ruleId = fix.RuleId;

		SayStart(fix);

		var fixOptions = BuildRuleFixOptions(fix);

		using var sessionLog = OpenSessionLog(row.RepositoryFullName, ruleId);
		fix.RecordedLogPath = sessionLog.Path;

		var session = new AiFixSession(
			new OllamaChatModel(client, ollama.Model!, ollama.ContextWindow, ollama.ToDirect()),
			fix.Toolbox!,
			fixOptions,
			Say,
			// Straight into this item's transcript rather than through Say: a token is a fragment,
			// not a line, and the shared console has no way to render a hundred of them a second.
			AiTranscriptSink.For(fix.Item.Transcript),
			sessionLog,
			onStatus: fix.Progress.Report,
			statusDetail: () => fix.CoverageNow);

		var request = BuildRuleFixRequest(fix);

		fix.RecordedRequest = request;
		LogSessionStart(sessionLog, request, ollama, fixOptions, writablePaths: fix.CoverageMode ? fix.TestFolders : null);

		// Into the transcript before the first turn, so the pane reads in the order the model saw
		// things. Not through Say: the shared console is not the place for two pages of prompt.
		fix.Item.Transcript.Append(WorkLineKind.Prompt, $"System prompt:\n{request.SystemPrompt}");
		fix.Item.Transcript.Append(WorkLineKind.Prompt, $"Task:\n{request.Task}");

		return await session
			.RunAsync(request, token => CheckRuleFixAsync(fix, token), cancellationToken)
			.ConfigureAwait(false);
	}

	private void SayStart(RuleFixContext fix)
	{
		var targetPath = fix.TargetPath;

		Say($"▶ {fix.RuleId} on {fix.Row.RepositoryFullName} via {fix.Ollama.Model}"
			+ $"{(targetPath is { Length: > 0 } ? $", in {targetPath}" : string.Empty)}"
			+ $"{(fix.Playbook is null ? " (no playbook — using the advisory)" : string.Empty)}.");

		if (fix.CoverageMode)
		{
			Say($"ℹ️ {fix.RuleId} is graded by Codacy on the published branch, so this run is judged on local line "
				+ "coverage from the same tests. Codacy re-grades after you push.");
		}
		else if (fix.RemotelyGraded)
		{
			Say($"ℹ️ {fix.RuleId} is graded on the published branch, so this run is judged on whether the "
				+ "change builds. Codacy re-grades after you push.");
		}
	}

	private static AiFixOptions BuildRuleFixOptions(RuleFixContext fix)
	{
		var ollama = fix.Ollama;
		var coverageMode = fix.CoverageMode;

		return new AiFixOptions
		{
			// Writing tests is many small steps — read a file, write a test, measure — where a one-line fix
			// is one or two, and a budget sized for the second starves the first.
			MaxTurnsPerAttempt = coverageMode ? Math.Max(ollama.MaxTurnsPerAttempt, _coverageTurnsPerAttempt) : ollama.MaxTurnsPerAttempt,

			// A coverage figure is fresh feedback every attempt, unlike a remote verdict that repeats itself.
			// A remote verdict repeats itself, so a second attempt would be told nothing new — unless the
			// tests are being checked, which gives it something specific to fix.
			MaxAttempts = fix.RemotelyGraded && !coverageMode && !fix.VerifyTests ? 1 : ollama.MaxAttemptsPerRule,

			// A clock as well as a turn count: a turn that writes a whole test file takes most of a minute.
			TimeBudget = coverageMode ? _coverageTimeBudget : _sessionTimeBudget
		};
	}

	private static AiFixRequest BuildRuleFixRequest(RuleFixContext fix)
	{
		var row = fix.Row;
		var baseline = fix.Baseline;
		var duplicationBefore = fix.DuplicationBefore;

		return new AiFixRequest(
			row.RepositoryFullName,
			fix.RuleId,
			fix.Result.RuleName,
			AiFixPrompt.BuildTask(fix.Result, row.RepositoryFullName, fix.Playbook, fix.TargetPath)
				+ (baseline?.Report is not null
					? $"\nTest project folder (the only place you may write): {string.Join(", ", fix.TestFolders)}\n"
						+ "Existing files there are read-only: add new test files, with new names.\n"
						+ $"Measured now:\n{DescribeCoverage(baseline, fix.KnownFailures)}\n"
					: string.Empty)
				+ (duplicationBefore is not null
					? $"\n{DuplicationMeasurement.Describe(duplicationBefore, 8)}"
						+ "The same code appears in more than one file, so changing only this file cannot remove it. "
						+ $"You may edit this file and these files that share it: {string.Join(", ", duplicationBefore.PeerFiles)}. "
						+ "You may also add one new file in the same folder to hold the shared code. "
						+ "Joining lines, moving braces or renaming does not reduce duplication and will not be counted: "
						+ "put each repeated run in one place and use it from every copy.\n"
					: string.Empty),
			AiFixPrompt.SystemPrompt);
	}

	private Task<AiRuleCheck> CheckRuleFixAsync(RuleFixContext fix, CancellationToken token)
	{
		var row = fix.Row;

		if (fix.CoverageMode)
		{
			return CheckCoverageAsync(row, fix.Toolbox!, fix.KnownFailures, status => fix.CoverageNow = status, token);
		}

		if (!fix.RemotelyGraded)
		{
			return ReEvaluateAndBuildAsync(row, fix.RuleId, fix.Toolbox!, fix.BuildWasBroken, token);
		}

		var duplicationBefore = fix.DuplicationBefore;

		return CheckLocalChangeAsync(
			row,
			fix.Toolbox!,
			fix.VerifyTests ? fix.TestBaseline : null,
			token,
			duplicationBefore is null ? null : (duplicationBefore, fix.TargetPath!));
	}

	/// <summary>Says what the session achieved and, when it succeeded, hands the change on for delivery.</summary>
	private async Task ReportOutcomeAsync(RuleFixContext fix, AiFixOutcome outcome, IDisposable hold, CancellationToken cancellationToken)
	{
		var row = fix.Row;
		var ruleId = fix.RuleId;
		var toolbox = fix.Toolbox!;

		if (fix.Succeeded && fix.RemotelyGraded)
		{
			// Deliberately no ReplaceRuleResult: the rule still says what Codacy last told it, and
			// painting it green here would claim a verdict nothing has issued.
			Say($"✅ {string.Join(", ", toolbox.FilesWritten)} changed and the build still passes. "
				+ $"{ruleId} stays as it is until Codacy re-grades the merged branch.");
		}
		else if (fix.Succeeded)
		{
			// The one rule is re-evaluated rather than the whole repository: it is the only thing that
			// can have changed, and a full re-assessment here would cost far more than the fix did.
			var after = await ReEvaluateAsync(row, ruleId, cancellationToken).ConfigureAwait(false);
			ReplaceRuleResult(row, ruleId, after);

			Say($"✅ {outcome.Summary}");
		}

		if (!fix.Succeeded)
		{
			return;
		}

		// The model is finished, so its server is released before delivery: running the suite and
		// waiting for CI can take half an hour, and nobody else's fix should queue behind that.
		hold.Dispose();

		// Handed on rather than left for review: the tests, a pull request and CI are the evidence
		// now, and the pipeline names whichever gate stopped it.
		var delivered = await DeliverAiChangeAsync(
			fix.Item,
			row,
			AiChangeKind.RuleFix,
			$"rule {ruleId}",
			$"Fix {ruleId}: {fix.Result.RuleName}",
			toolbox.FilesWritten,
			new HashSet<string>(fix.KnownFailures.Concat(fix.TestBaseline), StringComparer.Ordinal),
			cancellationToken).ConfigureAwait(false);

		if (delivered.Kind == AiChangeOutcomeKind.Refused)
		{
			Say("↩️ The edits are still in the working tree: they were not delivered, and nothing else was touched.");
		}

		await dashboard.RefreshGitStatusAsync(row, CancellationToken.None).ConfigureAwait(false);
		cache.UpsertRow(row);
	}

	/// <summary>Whatever happened, puts back what a failed session wrote (or keeps real progress) and records the session.</summary>
	private async Task FinishRuleFixAsync(RuleFixContext fix, CancellationToken cancellationToken)
	{
		var row = fix.Row;

		// Taken before anything is put back: Undo forgets what the session wrote, and the diff of a
		// reverted change is exactly what a reviewer most wants to see.
		var writtenFiles = fix.Toolbox?.FilesWritten.Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
		var sessionDiff = await CaptureSessionDiffAsync(row, writtenFiles).ConfigureAwait(false);

		string? keptProgress = null;

		if (!fix.Succeeded)
		{
			keptProgress = await RevertOrKeepAsync(fix, cancellationToken).ConfigureAwait(false);
		}

		RecordSession(fix, keptProgress, writtenFiles, sessionDiff, cancellationToken.IsCancellationRequested);
	}

	private async Task<string> CaptureSessionDiffAsync(RepositoryDashboardRow row, List<string> writtenFiles)
		=> governance is not null && row.LocalPath is not null && writtenFiles.Count > 0
			? await governance.CaptureDiffAsync(row.LocalPath, writtenFiles, CancellationToken.None).ConfigureAwait(false)
			: string.Empty;

	private void RecordSession(RuleFixContext fix, string? keptProgress, List<string> writtenFiles, string sessionDiff, bool cancelled)
	{
		if (governance is null || fix.RecordedRequest is null)
		{
			return;
		}

		governance.RecordAiSession(
			fix.Item,
			fix.RecordedRequest,
			runtimeSettings.Ollama.Model,
			fix.RecordedOutcome,
			SessionVerdict(fix.Succeeded, keptProgress, cancelled),
			writtenFiles,
			sessionDiff,
			fix.RecordedLogPath);
	}

	internal static string SessionVerdict(bool succeeded, string? keptProgress, bool cancelled)
	{
		if (succeeded)
		{
			return "applied";
		}

		if (cancelled)
		{
			return "cancelled";
		}

		return keptProgress is not null ? "kept" : "reverted";
	}

	/// <returns>What progress was kept, or null when the session's writes were put back.</returns>
	private async Task<string?> RevertOrKeepAsync(RuleFixContext fix, CancellationToken cancellationToken)
	{
		// Not on a stop the user asked for: that is a request to put things back as they were.
		var keptProgress = fix.ProgressWorthKeeping is not null && !cancellationToken.IsCancellationRequested
			? await fix.ProgressWorthKeeping().ConfigureAwait(false)
			: null;

		if (keptProgress is not null)
		{
			Say($"📈 Coverage went from {keptProgress} — short of the target, but the tests compile and pass, "
				+ "so they are kept. Run it again to carry on from here.");
		}
		else
		{
			// Only what this session wrote, not everything the clone has changed. Discarding the lot
			// also destroys an earlier fix still waiting to be reviewed, which is how a failed run on
			// one rule came to threaten the successful run on another.
			var undone = fix.UndoWrites?.Invoke() ?? 0;

			Say(undone == 0
				? "↩️ Nothing this session wrote needed putting back."
				: $"↩️ Put back {undone} file(s) this session had written; other changes in the clone were left alone.");
		}

		await dashboard.RefreshGitStatusAsync(fix.Row, CancellationToken.None).ConfigureAwait(false);
		return keptProgress;
	}
}
