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
	/// <summary>What a build and test of the uncommitted changes concluded.</summary>
	/// <param name="Passed">Whether the changes are safe to commit.</param>
	/// <param name="Summary">What to tell the model, or the user, about it.</param>
	private sealed record UnstickCheck(bool Passed, string Summary);

	/// <summary>
	/// Gets one clone out from behind uncommitted changes that stop it syncing.
	/// </summary>
	/// <remarks>
	/// Order matters, and each step is the cheapest thing that could end the problem. The changes are built
	/// and tested as they stand, because most are fine and only needed committing. Only if they are not does
	/// the local model get involved, confined to the changed files, with a way to put a file back. And only
	/// if the model cannot manage either is the work stashed — set aside, never discarded — which is what
	/// lets the clone sync at all. Nothing here can lose work: the worst outcome is a line in
	/// <c>git stash list</c>.
	/// <para>
	/// A test that fails on the changed tree only counts against the changes if it passed on the committed
	/// one, and that baseline is measured lazily: it costs a second test run, which is minutes in a library
	/// whose tests call a live service, so it is paid only when something fails.
	/// </para>
	/// </remarks>
	/// <param name="item">The item naming the repository to unstick.</param>
	/// <param name="progress">Reports the model's progress while it works.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task UnstickWithAiAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		if (row.LocalPath is null || !row.IsClonedLocally)
		{
			Say($"⏭️ {row.RepositoryFullName} is not cloned locally, so there is nothing to unstick.");
			return;
		}

		try
		{
			await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);

			if (row.IsWorkingTreeClean != false)
			{
				Say("✅ The working tree is already clean.");
				item.Succeeded = await SyncAfterUnstickAsync(row, cancellationToken).ConfigureAwait(false);
				return;
			}

			var changed = await dashboard
				.GetWorkingTreeStatusPreviewAsync(row, int.MaxValue, cancellationToken)
				.ConfigureAwait(false);

			Say($"▶ {changed.Count} uncommitted change(s) in {row.RepositoryFullName} are stopping it syncing:");
			foreach (var line in changed.Take(12))
			{
				Say($"  {line}");
			}

			if (changed.Count > 12)
			{
				Say($"  ...and {changed.Count - 12} more.");
			}

			item.Succeeded = await JudgeAndDeliverUnstickAsync(item, row, changed, progress, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			Say("⏹️ Stopped. Any changes still in the working tree are left as they are.");
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
			item.Succeeded = false;
		}
	}

	private sealed class TestBaseline
	{
		public IReadOnlySet<string>? Value { get; set; }
	}

	private async Task<bool> JudgeAndDeliverUnstickAsync(
		WorkItem item,
		RepositoryDashboardRow row,
		IReadOnlyList<string> changed,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		var baseline = new TestBaseline();

		Say("🔨 Building and testing the changes as they stand...");
		var check = await JudgeUnstickAsync(row, baseline, cancellationToken).ConfigureAwait(false);

		var resolved = check.Passed;

		if (resolved)
		{
			Say($"✅ {check.Summary} Committing them as they are.");
		}
		else
		{
			Say($"⚠️ These changes are not safe to commit as they are: {FirstLine(check.Summary)}");

			resolved = await RepairUncommittedWithModelAsync(
					item,
					row,
					changed,
					check.Summary,
					token => JudgeUnstickAsync(row, baseline, token),
					progress,
					cancellationToken)
				.ConfigureAwait(false);
		}

		var pushed = resolved
			&& await DeliverUnstuckChangesAsync(item, row, changed, baseline.Value ?? new HashSet<string>(), cancellationToken)
				.ConfigureAwait(false);

		if (!pushed)
		{
			await SetChangesAsideAsync(row).ConfigureAwait(false);
		}

		return await SyncAfterUnstickAsync(row, cancellationToken).ConfigureAwait(false);
	}

	private async Task<UnstickCheck> JudgeUnstickAsync(RepositoryDashboardRow row, TestBaseline baseline, CancellationToken token)
	{
		var buildOutput = new List<string>();
		await dashboard.BuildAsync(row, buildOutput.Add, token, restore: true).ConfigureAwait(false);

		var built = row.Status == PackageStatus.BuildSucceeded;
		RememberBuildResult(row, built ? RepositoryBuildState.Succeeded : RepositoryBuildState.Failed);

		if (!built)
		{
			return new UnstickCheck(false, SummariseRun("Build", false, buildOutput));
		}

		var testOutput = new List<string>();
		await dashboard.RunTestsAsync(row, testOutput.Add, token, sandbox: true, null).ConfigureAwait(false);

		if (row.Status == PackageStatus.TestsPassed)
		{
			return new UnstickCheck(true, "The build succeeds and the tests pass.");
		}

		var failed = CoverageMeasurement.ParseFailedTests(string.Join('\n', testOutput));

		if (failed.Count == 0)
		{
			return new UnstickCheck(false, SummariseRun("Tests", false, testOutput));
		}

		baseline.Value ??= await MeasureTestBaselineAsync(row, token).ConfigureAwait(false);

		var added = failed
			.Where(name => !baseline.Value.Contains(name))
			.Order(StringComparer.Ordinal)
			.ToList();

		return added.Count == 0
			? new UnstickCheck(
				true,
				$"{failed.Count.ToString(CultureInfo.InvariantCulture)} test(s) fail, and every one failed before "
				+ "these changes too.")
			: new UnstickCheck(
				false,
				"These tests passed before the changes and fail now: "
				+ $"{string.Join(", ", added.Take(10))}.\n{SummariseRun("Tests", false, testOutput)}");
	}

	private static string FirstLine(string text)
		=> text.Split('\n', 2)[0].Trim();

	/// <summary>
	/// Which tests fail on the last commit, found by setting the uncommitted changes aside for the duration.
	/// </summary>
	/// <remarks>
	/// The changes are stashed and brought straight back in a <c>finally</c>, so a failure or a stop in the
	/// middle cannot leave them in the stash unannounced. If the stash itself fails the baseline is empty,
	/// which is the cautious answer: every failing test then counts against the changes.
	/// </remarks>
	private async Task<IReadOnlySet<string>> MeasureTestBaselineAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		Say("🧪 Some tests fail. Finding out which of them already failed before these changes...");

		var (stashed, stashOutput) = await localRepo
			.StashLocalChangesAsync(row.RepositoryFullName, "unstick: measuring which tests already fail", cancellationToken)
			.ConfigureAwait(false);

		if (!stashed)
		{
			Say($"⚠️ Could not set the changes aside to measure that ({FirstLine(stashOutput)}). Counting every failure against them.");
			return new HashSet<string>();
		}

		try
		{
			var buildOutput = new List<string>();
			await dashboard.BuildAsync(row, buildOutput.Add, cancellationToken, restore: true).ConfigureAwait(false);

			if (row.Status != PackageStatus.BuildSucceeded)
			{
				Say("ℹ️ The last commit does not build here either, so no test can be said to have passed on it.");
				return new HashSet<string>();
			}

			var testOutput = new List<string>();
			await dashboard.RunTestsAsync(row, testOutput.Add, cancellationToken, sandbox: true, null).ConfigureAwait(false);

			var failed = CoverageMeasurement.ParseFailedTests(string.Join('\n', testOutput));
			Say($"ℹ️ {failed.Count.ToString(CultureInfo.InvariantCulture)} test(s) already fail on the last commit.");
			return failed;
		}
		finally
		{
			var (restored, popOutput) = await localRepo
				.PopStashAsync(row.RepositoryFullName, CancellationToken.None)
				.ConfigureAwait(false);

			Say(restored
				? "↩️ The changes are back in the working tree."
				: $"⚠️ Could not bring the changes back automatically ({FirstLine(popOutput)}). They are in the newest entry of "
					+ $"`git stash list` in {row.LocalPath}.");
		}
	}

	private AiFixToolbox CreateRepairToolbox(RepositoryDashboardRow row, IReadOnlyList<string> files, IReadOnlyList<string> folders)
		=> new(
			row.LocalPath!,
			build: async token =>
			{
				var captured = new List<string>();
				await dashboard.BuildAsync(row, captured.Add, token, restore: true).ConfigureAwait(false);
				return SummariseRun("Build", row.Status == PackageStatus.BuildSucceeded, captured);
			},
			writablePaths: files.Count + folders.Count == 0 ? null : files,
			writableFolders: folders.Count == 0 ? null : folders,

			// The oracle runs the tests, with the baseline; a bare run_tests would show the model failures it
			// did not cause, which is how a session once came to edit a library's integration tests.
			offerRunTests: false,
			restoreFile: (path, token) => localRepo.RestoreFileAsync(row.RepositoryFullName, path, token));

	/// <summary>
	/// Lets the local model repair the uncommitted changes, confined to the files they touch.
	/// </summary>
	/// <returns>Whether the tree now builds and passes, and so is safe to commit.</returns>
	private async Task<bool> RepairUncommittedWithModelAsync(
		WorkItem item,
		RepositoryDashboardRow row,
		IReadOnlyList<string> changed,
		string problem,
		Func<CancellationToken, Task<UnstickCheck>> judge,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		var ollama = runtimeSettings.Ollama;

		if (!ollama.IsConfigured)
		{
			Say("ℹ️ No Ollama server is configured, so nothing can repair them.");
			return false;
		}

		var (files, folders) = UnstickPrompt.PathsIn(changed);
		var succeeded = false;
		AiFixToolbox? toolbox = null;

		try
		{
			Say($"⏳ Waiting for the model ({ollama.Model})...");
			using var hold = await ollamaGate.EnterAsync(cancellationToken).ConfigureAwait(false);

			toolbox = CreateRepairToolbox(row, files, folders);

			var request = new AiFixRequest(
				row.RepositoryFullName,
				"unstick",
				"Uncommitted changes",
				UnstickPrompt.BuildTask(row.RepositoryFullName, changed, problem),
				AiFixPrompt.SystemPrompt);

			succeeded = await RunRepairSessionAsync(item, row, toolbox, request, [.. files, .. folders], judge, progress, cancellationToken)
				.ConfigureAwait(false);

			return succeeded;
		}
		finally
		{
			if (!succeeded && toolbox is not null)
			{
				// Back to the uncommitted state this item found, not to the last commit: the stash that
				// follows takes whatever is there, and a half-repair would only make that harder to read.
				var restored = toolbox.Undo();
				if (restored > 0)
				{
					Say($"↩️ Put {restored} file(s) back as the model found them.");
				}
			}
		}
	}

	private async Task<bool> RunRepairSessionAsync(
		WorkItem item,
		RepositoryDashboardRow row,
		AiFixToolbox toolbox,
		AiFixRequest request,
		IReadOnlyList<string> paths,
		Func<CancellationToken, Task<UnstickCheck>> judge,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		var ollama = runtimeSettings.Ollama;

		using var client = CreateOllamaClient(ollama);

		var fixOptions = new AiFixOptions
		{
			MaxTurnsPerAttempt = ollama.MaxTurnsPerAttempt,
			MaxAttempts = ollama.MaxAttemptsPerRule,
			TimeBudget = _sessionTimeBudget
		};

		using var sessionLog = OpenSessionLog(row.RepositoryFullName, "unstick");

		var session = new AiFixSession(
			new OllamaChatModel(client, ollama.Model!, ollama.ContextWindow, ollama.ToDirect()),
			toolbox,
			fixOptions,
			Say,
			AiTranscriptSink.For(item.Transcript),
			sessionLog,
			onStatus: progress.Report);

		LogSessionStart(sessionLog, request, ollama, fixOptions, paths);

		item.Transcript.Append(WorkLineKind.Prompt, $"System prompt:\n{request.SystemPrompt}");
		item.Transcript.Append(WorkLineKind.Prompt, $"Task:\n{request.Task}");

		Say($"▶ Repairing the uncommitted changes in {row.RepositoryFullName} via {ollama.Model}.");

		var outcome = await session
			.RunAsync(
				request,
				async token =>
				{
					var verdict = await judge(token).ConfigureAwait(false);
					Say(verdict.Passed ? $"   ✓ {verdict.Summary}" : $"   ⚠️ {FirstLine(verdict.Summary)}");
					return new AiRuleCheck(verdict.Passed, verdict.Summary);
				},
				cancellationToken)
			.ConfigureAwait(false);

		Say(outcome.Succeeded
			? "✅ The model got the changes building and passing."
			: "❌ The model could not get the changes building and passing.");

		return outcome.Succeeded;
	}

	/// <summary>
	/// Delivers changes that built and passed through the AI change pipeline: a branch, a pull request, CI and a merge.
	/// </summary>
	/// <param name="item">The work item, which keeps a copy of the change for the Changes tab.</param>
	/// <param name="row">The repository.</param>
	/// <param name="changed">The <c>git status --porcelain</c> lines of what is uncommitted.</param>
	/// <param name="knownFailures">Tests that already failed on the last commit, which do not count against the changes.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	/// <returns>Whether the pull request was merged.</returns>
	/// <remarks>
	/// The same route every other AI-touched change takes, rather than a straight push to the default branch:
	/// these changes were made by tools nobody reviewed and, often, repaired by a model, which is exactly the
	/// case the pipeline's gates exist for. The pipeline puts the clone back to main whatever happens, and
	/// that discards an undelivered edit, so the changes are first recorded in the stash list — the tree
	/// untouched — and the entry is only dropped once they are safely merged.
	/// </remarks>
	private async Task<bool> DeliverUnstuckChangesAsync(
		WorkItem item,
		RepositoryDashboardRow row,
		IReadOnlyList<string> changed,
		IReadOnlySet<string> knownFailures,
		CancellationToken cancellationToken)
	{
		var snapshotName = $"unstick snapshot {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC";

		var snapshotted = await localRepo
			.SnapshotLocalChangesAsync(row.RepositoryFullName, snapshotName, CancellationToken.None)
			.ConfigureAwait(false);

		if (!snapshotted)
		{
			// Not delivered without a way back: a refusal here costs a retry, and losing the repair costs the repair.
			Say("⚠️ Could not make a safety copy of the changes, so they were not delivered.");
			return false;
		}

		var (files, folders) = UnstickPrompt.PathsIn(changed);

		// A folder git reported whole holds files the pipeline must be told about one by one.
		var owned = files
			.Concat(folders.SelectMany(folder =>
				Directory.EnumerateFiles(Path.Combine(row.LocalPath!, folder), "*", SearchOption.AllDirectories)
					.Select(file => Path.GetRelativePath(row.LocalPath!, file).Replace('\\', '/'))))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var outcome = await DeliverAiChangeAsync(
			item,
			row,
			AiChangeKind.RuleFix,
			"unstick",
			$"Commit reviewed remediation edits for {row.RepositoryFullName}",
			owned,
			knownFailures,
			cancellationToken).ConfigureAwait(false);

		if (outcome.Kind == AiChangeOutcomeKind.Merged)
		{
			await localRepo.DropSnapshotAsync(row.RepositoryFullName, snapshotName, CancellationToken.None).ConfigureAwait(false);
			Say($"✅ {outcome.Message}");
			return true;
		}

		Say($"⚠️ Not merged ({outcome.Kind}): {outcome.Message} The changes are kept in `git stash list` as \"{snapshotName}\".");
		return false;
	}

	/// <summary>
	/// Sets everything uncommitted aside in a git stash so the clone can sync, and says exactly where it went.
	/// </summary>
	private async Task SetChangesAsideAsync(RepositoryDashboardRow row)
	{
		var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

		var (stashed, output) = await localRepo
			.StashLocalChangesAsync(
				row.RepositoryFullName,
				$"unstick {stamp} UTC: uncommitted changes that could not be made to build and pass",
				CancellationToken.None)
			.ConfigureAwait(false);

		Say(stashed
			? "📦 Set the changes aside in a git stash — nothing was thrown away. To look at them: "
				+ $"`git stash list` and `git stash show -p` in {row.LocalPath}; to bring them back: `git stash pop`."
			: output.Contains("No local changes", StringComparison.OrdinalIgnoreCase)
				// Delivery puts the clone back to main when it fails, which empties the tree — but not before the
				// changes were copied into the stash list, so there is still nothing lost.
				? $"ℹ️ The working tree is already clean. Any changes it held are in `git stash list` in {row.LocalPath}."
				: $"⚠️ Could not set the changes aside: {FirstLine(output)}");

		await dashboard.RefreshGitStatusAsync(row, CancellationToken.None).ConfigureAwait(false);
	}

	/// <summary>Syncs the clone with its remote once it is clean, and reports where it stands.</summary>
	private async Task<bool> SyncAfterUnstickAsync(RepositoryDashboardRow row, CancellationToken cancellationToken)
	{
		var synced = await dashboard.GitSyncAsync(row, Say, cancellationToken).ConfigureAwait(false);

		Say(synced ? "✅ Git sync complete." : "❌ Git sync failed.");
		cache.UpsertRow(row);

		if (row.CurrentBranch is not null)
		{
			Say($"ℹ️ Branch: {row.CurrentBranch} | Clean: {row.IsWorkingTreeClean} | Synced: {row.IsSyncedWithOrigin}");
		}

		return synced;
	}
}
