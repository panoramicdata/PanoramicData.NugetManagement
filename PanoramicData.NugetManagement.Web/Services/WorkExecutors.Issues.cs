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
	private static IssueAnalysisInput BuildAnalysisInput(RepositoryDashboardRow row, RepositoryIssue issue, GitHubIssueThread thread)
		=> new(
			row.RepositoryFullName,
			thread.Number,
			thread.Title,
			thread.Body,
			thread.AuthorLogin,
			thread.AuthorAssociation,
			thread.CreatedAtUtc,
			issue.LastMaintainerReplyUtc,
			[.. thread.Comments.Select(comment => new IssueAnalysisComment(
				comment.AuthorLogin, comment.AuthorAssociation, comment.CreatedAtUtc, comment.Body))],
			RepositoryFileList(row),
			[.. row.Assessment?.RuleResults.Where(result => !result.Passed).Select(result => result.RuleId)
				?? []]);

	/// <summary>
	/// Reads one human-raised issue and records what should happen about it.
	/// </summary>
	/// <remarks>
	/// The whole of this application's exposure to text a stranger wrote lives in this method, and the
	/// containment is that the session it starts holds no tools. Nothing read here can cause a file to
	/// be opened, a comment to be posted or a request to leave the machine — the only thing that comes
	/// back is a verdict, which is parsed into closed types and then gated in C#.
	/// <para>
	/// What follows the verdict is deliberately asymmetric. A fix that clears the gate is queued, an
	/// answer that clears it is posted, and a rejection is never carried out here at any confidence:
	/// closing somebody's issue is published under the organisation's name and waits for a person.
	/// </para>
	/// </remarks>
	private async Task AnalyseIssueAsync(
		WorkItem item,
		CancellationToken cancellationToken)
	{
		var row = RowFor(item);

		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var issueNumber = int.Parse(item.Descriptor.Parameter("issueNumber")!, CultureInfo.InvariantCulture);
		var ollama = runtimeSettings.Ollama;

		if (!ollama.IsConfigured)
		{
			Say("⚠️ No Ollama server is configured — set one under Settings, Ollama Config.");
			return;
		}

		var issue = row.OpenIssues.FirstOrDefault(candidate => candidate.Number == issueNumber);

		if (issue is null)
		{
			Say($"⏭️ Issue #{issueNumber} is no longer open on {row.RepositoryFullName}.");
			return;
		}

		// Re-checked rather than trusted from the queue: an item can sit in a lane while the world
		// changes, and analysing a bot's issue would have two passes judging the same item in public.
		if (!HumanIssueFilter.IsHumanRaised(issue))
		{
			Say($"⏭️ #{issueNumber} is not a human-raised issue.");
			return;
		}

		var github = await CreateGitHubClientAsync().ConfigureAwait(false);
		var issueApi = new OctokitGitHubIssueApi(github);
		var (owner, name) = SplitFullName(row.RepositoryFullName);

		var thread = await issueApi
			.GetThreadAsync(owner, name, issueNumber, cancellationToken)
			.ConfigureAwait(false);

		var input = BuildAnalysisInput(row, issue, thread);

		Say($"⏳ Waiting for the model ({ollama.Model})...");
		using var hold = await ollamaGate.EnterAsync(cancellationToken).ConfigureAwait(false);

		using var client = new Ollama.Api.OllamaClient(new Ollama.Api.OllamaClientOptions
		{
			Uri = new Uri(ollama.BaseUrl!),
			ApiKey = ollama.ApiKey,
			Timeout = TimeSpan.FromMilliseconds(ollama.RequestTimeoutMs)
		});

		var session = new IssueAnalysisSession(
			new OllamaChatModel(client, ollama.Model!, ollama.ContextWindow),
			Say,
			AiTranscriptSink.For(item.Transcript));

		item.Transcript.Append(WorkLineKind.Prompt, $"System prompt:\n{IssueAnalysisSession.SystemPrompt}");
		item.Transcript.Append(
			WorkLineKind.Prompt,
			$"Task:\n{IssueAnalysisSession.BuildTask(input, DateTimeOffset.UtcNow)}");

		var verdict = await session
			.AnalyseAsync(input, DateTimeOffset.UtcNow, cancellationToken)
			.ConfigureAwait(false);

		issue.Analysis = verdict;
		issue.AnalysedAtIssueUpdatedUtc = thread.Comments.Count > 0
			? thread.Comments[^1].CreatedAtUtc
			: thread.CreatedAtUtc;

		cache.UpsertRow(row);

		await ActOnVerdictAsync(item, row, issue, thread, verdict, github, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Does whatever the gate permits about a verdict, and says what it left for a human.
	/// </summary>
	private async Task ActOnVerdictAsync(
		WorkItem item,
		RepositoryDashboardRow row,
		RepositoryIssue issue,
		GitHubIssueThread thread,
		IssueVerdict verdict,
		IGitHubClient github,
		CancellationToken cancellationToken)
	{
		if (verdict.Action is IssueAction.Fix)
		{
			var decision = IssueAutonomyGate.MayFixAutomatically(
				verdict,
				thread.AuthorAssociation,
				row.LocalPath ?? string.Empty,
				RemediationOwnedPaths(row));

			if (decision.Allowed)
			{
				fanOut.EnqueueIssueFix(
					row.Organization, row.RepositoryFullName, issue.Number, item.ConsoleNodeKey);

				Say($"▶ #{issue.Number}: queued a fix. {decision.Reason}");
				return;
			}

			Say($"⏸️ #{issue.Number}: a fix is proposed but waits for you — {decision.Reason}");
			return;
		}

		if (verdict.Action is IssueAction.Answer)
		{
			var decision = IssueAutonomyGate.MayAnswerAutomatically(verdict);

			if (decision.Allowed)
			{
				var (owner, name) = SplitFullName(row.RepositoryFullName);

				await new OctokitGitHubWriteApi(github)
					.CommentAsync(owner, name, issue.Number, verdict.DraftReply!, cancellationToken)
					.ConfigureAwait(false);

				Say($"💬 #{issue.Number}: replied. {decision.Reason}");
				return;
			}

			Say($"⏸️ #{issue.Number}: a reply is drafted but waits for you — {decision.Reason}");
			return;
		}

		// Reject and Escalate both stop here, and Reject stops here however sure the model was.
		// Closing a real person's issue — or saying in public that it looked like an attack — is not
		// worth automating, and a rejection is also exactly what somebody would aim for who wanted a
		// genuine report closed unread.
		Say(verdict.Action is IssueAction.Reject
			? $"⏸️ #{issue.Number}: a rejection is drafted and waits for you. Rejections are never posted "
				+ "automatically."
			: $"⏸️ #{issue.Number}: left for you. {verdict.Reasoning}");
	}

	/// <summary>
	/// Acts on the brief one analysis produced, seeing the brief and never the issue.
	/// </summary>
	/// <remarks>
	/// Two things make this different from a rule-driven fix. The session may write only the files the
	/// brief named, because there is no rule to notice it wandering; and a failed attempt is undone,
	/// because a rule-driven mess announces itself at the next assessment while this one would not.
	/// </remarks>
	private async Task FixWithAiIssueAsync(
		WorkItem item,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		var row = RowFor(item);

		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var issueNumber = int.Parse(item.Descriptor.Parameter("issueNumber")!, CultureInfo.InvariantCulture);
		var ollama = runtimeSettings.Ollama;

		if (!ollama.IsConfigured)
		{
			Say("⚠️ No Ollama server is configured — set one under Settings, Ollama Config.");
			return;
		}

		if (row.LocalPath is null || !row.IsClonedLocally)
		{
			Say($"⏭️ {row.RepositoryFullName} is not cloned locally, and the model edits files on disk.");
			return;
		}

		if (PlanIssueFix(row, issueNumber) is not { } plan)
		{
			return;
		}

		try
		{
			using var client = CreateOllamaClient(ollama);

			await RecordPreExistingChangesAsync(item, row.LocalPath, cancellationToken).ConfigureAwait(false);

			var redToolbox = CreateRedToolbox(row, plan);
			var fixToolbox = CreateFixToolbox(row, plan);
			var oracle = CreateIssueOracle(row);

			var fixOptions = new AiFixOptions
			{
				MaxTurnsPerAttempt = ollama.MaxTurnsPerAttempt,
				MaxAttempts = ollama.MaxAttemptsPerRule
			};

			using var redLog = OpenSessionLog(row.RepositoryFullName, $"issue-{issueNumber}-red");
			using var fixLog = OpenSessionLog(row.RepositoryFullName, $"issue-{issueNumber}-fix");

			AiFixSession NewSession(AiFixToolbox toolbox, AiSessionLog log) => new(
				new OllamaChatModel(client, ollama.Model!, ollama.ContextWindow, ollama.ToDirect()),
				toolbox,
				fixOptions,
				Say,
				AiTranscriptSink.For(item.Transcript),
				log,
				onStatus: progress.Report);

			var brief = plan.Brief;

			// The report as the model sees it: the brief's paraphrase, never the reporter's own words.
			var issueText = $"Goal: {brief.Goal}\nDone looks like: {brief.ExpectedEndState}";

			var (owner, name) = SplitFullName(row.RepositoryFullName);

			var change = new AiChangeRequest(
				owner,
				name,
				AiChangeKind.IssueFix,
				AiChangePipelineFactory.Slug($"issue {issueNumber}", Guid.NewGuid().ToString("N")[..6]),
				$"Fix #{issueNumber}: {AiChangePipelineFactory.Shorten(brief.Goal, 60)}",
				issueNumber,
				RequiresRedTest: IssueAutonomyGate.RequiresRedTest);

			var fixPrompt = IssueFixPrompt.BuildTask(brief, row.RepositoryFullName, issueNumber);

			LogSessionStart(
				fixLog,
				new AiFixRequest(row.RepositoryFullName, $"issue-{issueNumber}", $"Issue #{issueNumber}", fixPrompt, AiFixPrompt.SystemPrompt),
				ollama,
				fixOptions,
				brief.Paths);

			item.Transcript.Append(WorkLineKind.Prompt, $"System prompt:\n{AiFixPrompt.SystemPrompt}");
			item.Transcript.Append(WorkLineKind.Prompt, $"Task:\n{fixPrompt}");

			Say($"▶ #{issueNumber} on {row.RepositoryFullName} via {ollama.Model}: a failing test first in "
				+ $"{string.Join(", ", plan.TestFolders)}, then a fix writing only {string.Join(", ", brief.Paths)}.");

			// The model's server is held by the decorator around each call into the model, and not by this
			// method: delivery waits for CI, which can take half an hour, and that must not idle the GPU.
			var work = new GatedAiChangeWork(
				new AiSessionChangeWork(
					NewSession(redToolbox, redLog),
					NewSession(fixToolbox, fixLog),
					issueText,
					fixPrompt,
					AiFixPrompt.SystemPrompt,
					oracle.CheckAsync,
					token => CheckIssueTestCompilesAsync(row, token)),
				ollamaGate);

			var pipeline = AiChangePipelineFactory.Create(
				new LocalRepoAiChangeGit(row.LocalPath, row.RepositoryFullName),
				new DashboardAiChangeTests(dashboard, localRepo, row, row.RepositoryFullName),
				work,
				new OctokitGitHubWriteApi(await CreateGitHubClientAsync().ConfigureAwait(false)),
				AiChangePipelineFactory.IssueFixLimits(brief.Paths, plan.TestFolders),
				KeepDeliveredChange(item, row.LocalPath));

			var delivery = await pipeline.RunAsync(change, Say, cancellationToken).ConfigureAwait(false);
			NoteDeliveryOutcome(item, row, delivery);

			await dashboard.RefreshGitStatusAsync(row, CancellationToken.None).ConfigureAwait(false);
			cache.UpsertRow(row);
		}
		catch (OperationCanceledException)
		{
			Say($"⏹️ Stopped the fix for #{issueNumber}.");
			throw;
		}
	}

	private sealed record IssueFixPlan(IssueFixBrief Brief, IReadOnlyList<string> TestProjects, List<string> TestFolders);

	/// <summary>Works out what an issue fix may touch, saying why and returning null when it cannot go ahead.</summary>
	private IssueFixPlan? PlanIssueFix(RepositoryDashboardRow row, int issueNumber)
	{
		var brief = row.OpenIssues.FirstOrDefault(candidate => candidate.Number == issueNumber)?.Analysis?.Brief;

		if (brief is null)
		{
			Say($"⏭️ #{issueNumber} has no fix brief any more.");
			return null;
		}

		// Screened again here, and not only at the gate. The clone moves — another session commits, a
		// remediation rewrites a file, a branch changes underneath — and the allowlist this session is
		// about to be handed must describe the repository as it is now, not as it was when a model
		// looked at it.
		var screen = IssuePathScreen.Screen(row.LocalPath!, brief.Paths, RemediationOwnedPaths(row));

		if (!screen.Accepted)
		{
			Say($"⏭️ #{issueNumber}: {screen.Refusal}");
			return null;
		}

		// The reproducing test goes in the test project, and only there: the red-test step may add new test
		// files and nothing else, so a bug cannot be "reproduced" by editing the code it is meant to
		// exercise. A repository with no test project has nowhere to put one; it gets a project from the
		// TST-01 rule fix first, which is told how this estate builds one.
		var testProjects = CoverageMeasurement.FindTestProjects(row.LocalPath!);

		if (testProjects.Count == 0)
		{
			Say($"⏭️ #{issueNumber}: {row.RepositoryFullName} has no test project, so there is nowhere to write a "
				+ "failing test. Fix TST-01 first.");
			return null;
		}

		var testFolders = testProjects
			.Select(project => (Path.GetDirectoryName(project) ?? string.Empty).Replace('\\', '/'))
			.Where(folder => folder.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		return new IssueFixPlan(brief, testProjects, testFolders);
	}

	private static Ollama.Api.OllamaClient CreateOllamaClient(OllamaOptions ollama)
		=> new(new Ollama.Api.OllamaClientOptions
		{
			Uri = new Uri(ollama.BaseUrl!),
			ApiKey = ollama.ApiKey,
			Timeout = TimeSpan.FromMilliseconds(ollama.RequestTimeoutMs)
		});

	private Func<CancellationToken, Task<string>> BuildTool(RepositoryDashboardRow row)
		=> async token =>
		{
			var captured = new List<string>();
			await dashboard.BuildAsync(row, captured.Add, token, restore: true).ConfigureAwait(false);
			return SummariseRun("Build", row.Status == PackageStatus.BuildSucceeded, captured);
		};

	private Func<CancellationToken, Task<string>> TestTool(RepositoryDashboardRow row)
		=> async token =>
		{
			var captured = new List<string>();
			await dashboard.RunTestsAsync(row, captured.Add, token, sandbox: true, null).ConfigureAwait(false);
			return SummariseRun("Tests", row.Status == PackageStatus.TestsPassed, captured);
		};

	// Two toolboxes because the two steps may write different things: the first only new files in
	// the test folders, the second only the files the brief named.
	private AiFixToolbox CreateRedToolbox(RepositoryDashboardRow row, IssueFixPlan plan)
		=> new(
			row.LocalPath!,
			build: BuildTool(row),
			test: TestTool(row),
			writableFolders: plan.TestFolders,
			newFilesOnly: true,
			afterWrite: (path, token) => BuildFeedbackAsync(row.LocalPath!, plan.TestProjects, path, token));

	private AiFixToolbox CreateFixToolbox(RepositoryDashboardRow row, IssueFixPlan plan)
		=> new(
			row.LocalPath!,
			build: BuildTool(row),
			test: TestTool(row),
			writablePaths: plan.Brief.Paths);

	private IssueFixOracle CreateIssueOracle(RepositoryDashboardRow row)
		=> new(
			async token =>
			{
				var captured = new List<string>();
				await dashboard.BuildAsync(row, captured.Add, token, restore: true).ConfigureAwait(false);
				return new IssueFixCheckStep(
					row.Status == PackageStatus.BuildSucceeded, string.Join("\n", captured));
			},
			async token =>
			{
				var captured = new List<string>();
				await dashboard.RunTestsAsync(row, captured.Add, token, sandbox: true, null).ConfigureAwait(false);
				return new IssueFixCheckStep(
					row.Status == PackageStatus.TestsPassed, string.Join("\n", captured));
			});

	private async Task<AiRuleCheck> CheckIssueTestCompilesAsync(RepositoryDashboardRow row, CancellationToken token)
	{
		var captured = new List<string>();
		await dashboard.BuildAsync(row, captured.Add, token, restore: true).ConfigureAwait(false);
		var built = row.Status == PackageStatus.BuildSucceeded;

		return new AiRuleCheck(built, built ? "The test project builds." : string.Join('\n', captured.TakeLast(30)));
	}

	/// <summary>
	/// The repository's file paths, for an analysis that is given paths and never contents.
	/// </summary>
	private static IReadOnlyList<string> RepositoryFileList(RepositoryDashboardRow row)
	{
		if (row.LocalPath is null || !Directory.Exists(row.LocalPath))
		{
			return [];
		}

		var root = Path.GetFullPath(row.LocalPath);

		return
		[
			.. Directory
				.EnumerateFiles(root, "*", SearchOption.AllDirectories)
				.Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
						StringComparison.OrdinalIgnoreCase)
					&& !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
						StringComparison.OrdinalIgnoreCase)
					&& !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
						StringComparison.OrdinalIgnoreCase))
				.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
				.Order(StringComparer.OrdinalIgnoreCase)
				.Take(IssueAnalysisSession.MaxFilesListed)
		];
	}

	/// <summary>
	/// The files a deterministic remediation already maintains, which an issue fix may not also write.
	/// </summary>
	/// <remarks>
	/// Two writers for one file means a rule and an issue undoing each other's work on alternate runs,
	/// and the rule is the one with a specification behind it.
	/// </remarks>
	private IReadOnlyCollection<string> RemediationOwnedPaths(RepositoryDashboardRow row)
		=> [.. (row.Assessment?.RuleResults ?? [])
			.Where(result => !result.Passed && remediations.Get(result.RuleId) is not null)
			.SelectMany(result => result.Advisory?.Targets?.Select(target => target.Path) ?? [])
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)];

	private static (string Owner, string Name) SplitFullName(string repositoryFullName)
	{
		var parts = repositoryFullName.Split('/');
		return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
	}

	/// <summary>
	/// Discards whatever a stopped fix had half-written and reports what came of it.
	/// </summary>
	/// <param name="row">The repository whose part-applied changes to discard.</param>
	private async Task RevertPartAppliedFixAsync(RepositoryDashboardRow row)
	{
		var (success, discarded) = await localRepo.DiscardLocalChangesAsync(row.RepositoryFullName, CancellationToken.None).ConfigureAwait(false);
		Say(success
			? discarded.Count == 0
				? "↩️ Stopped before anything was written."
				: $"↩️ Reverted {discarded.Count} change(s) written before the stop."
			: "⚠️ Could not revert the part-applied changes — check the clone by hand.");

		await dashboard.RefreshGitStatusAsync(row, CancellationToken.None).ConfigureAwait(false);
	}

	private static string? TriageRefusal(RepositoryDashboardRow row)
	{
		if (row.Assessment is null)
		{
			return $"⚠️ {row.RepositoryFullName} has no assessment yet — assess it first, then triage.";
		}

		return row.OpenIssuesKnown
			? null
			: $"⚠️ {row.RepositoryFullName}'s open pull requests are not known — re-assess it first.";
	}

	/// <summary>
	/// Classifies one repository's open Dependabot pull requests and acts on the verdicts.
	/// </summary>
	/// <remarks>
	/// The only work that mutates GitHub. Every intended comment and close is said before it is made,
	/// so this item's output is the audit trail for it.
	/// <para>
	/// Needs a current assessment, because coverage is decided from which rules are failing. Without
	/// one it says so and stops, rather than guessing that nothing is failing and closing on that
	/// basis — an unassessed repository would otherwise look like one where nothing is covered, and
	/// raise a gap issue for every dependency Dependabot had ever mentioned.
	/// </para>
	/// </remarks>
	/// <param name="item">The item naming the repository to triage.</param>
	/// <param name="progress">Unused: triage reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task TriageDependabotAsync(
		WorkItem item,
		IProgress<string> progress,
		CancellationToken cancellationToken)
	{
		var row = RowFor(item);

		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		if (TriageRefusal(row) is { } refusal)
		{
			Say(refusal);
			return;
		}

		var dependabotPullRequests = row.OpenIssues
			.Where(issue => issue.IsPullRequest)
			.ToList();

		if (dependabotPullRequests.Count == 0)
		{
			Say($"✅ {row.RepositoryFullName} has no open pull requests to triage.");
			return;
		}

		Say($"▶ Triaging {dependabotPullRequests.Count} open pull request(s) for {row.RepositoryFullName}...");

		var github = await CreateGitHubClientAsync().ConfigureAwait(false);
		var context = await dashboard
			.BuildContextAsync(row, github, cancellationToken)
			.ConfigureAwait(false);

		var triages = new DependabotTriageService().Triage(
			dependabotPullRequests,
			context,
			row.Assessment!.RuleResults,
			ruleId => remediations.Get(ruleId) is not null);

		// Adoption writes files, which triage has never done. No clone means no adoption: the verdicts
		// are still reached and reported, and every adoptable pull request is left open saying why. The
		// guard matches the one the Fix lane uses, rather than inventing a second opinion about when a
		// clone can be written to.
		var adopter = row is { IsClonedLocally: true, LocalPath: not null }
			? new CloneBumpAdopter(row.LocalPath)
			: null;

		if (adopter is null)
		{
			Say($"ℹ️ {row.RepositoryFullName} is not cloned locally, so nothing can be adopted this pass.");
		}

		var outcome = await triageRunner
			.RunAsync(
				new OctokitGitHubIssueApi(github),
				new OctokitGitHubWriteApi(github),
				row.RepositoryFullName,
				triages,
				Say,
				cancellationToken,
				adopter)
			.ConfigureAwait(false);

		Say($"✅ {row.RepositoryFullName}: closed {outcome.Closed}, adopted {outcome.Adopted}, "
			+ $"closed {outcome.Obsolete} for dependencies it no longer references, "
			+ $"{outcome.Covered} awaiting an existing fix, {outcome.Uncovered} with no fix available, "
			+ $"{outcome.Unrecognised} left alone.");

		if (outcome.Adopted > 0)
		{
			// The adopted versions are sitting uncommitted in the clone. Saying so here is the only
			// warning anybody gets: the pull requests are already closed, so an unpushed clone is how
			// the bumps get lost.
			Say($"⚠️ {outcome.Adopted} adopted bump(s) are uncommitted in {row.LocalPath} — "
				+ "commit and push them, or the closed pull requests take the changes with them.");

			await dashboard.RefreshGitStatusAsync(row, cancellationToken).ConfigureAwait(false);
		}

		// The closed ones have left the open list, and the survivors now carry their verdicts.
		row.OpenIssues = [.. DependabotTriageRunner.Restamp(row.OpenIssues, triages)];

		cache.UpsertRow(row);
	}

	/// <summary>
	/// Adopts Dependabot bumps into one repository's local clone.
	/// </summary>
	/// <param name="localPath">The root of the clone.</param>
	/// <remarks>
	/// The whole of the runner's file-system dependency, so the runner itself keeps no knowledge of
	/// working trees and stays testable without one.
	/// </remarks>
	private sealed class CloneBumpAdopter(string localPath) : IBumpAdopter
	{
		/// <inheritdoc />
		public IReadOnlyList<string> Adopt(DependabotAdoptionPlan plan, Action<string> onOutput)
			=> DependabotAdoptionRemediation.Adopt(localPath, plan, onOutput);
	}
}
