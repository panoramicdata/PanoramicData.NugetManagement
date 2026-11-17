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

/// <summary>
/// Runs queued work. One method per <see cref="WorkKind"/>, none of which knows anything about a
/// browser.
/// </summary>
/// <remarks>
/// These bodies used to live in the dashboard component and were executed by the circuit that
/// queued them, which meant closing the tab cancelled the work and every body was free to reach
/// into the component's fields, clear its console and write to its clipboard. With up to twenty
/// lanes running at once none of that survives: there is no one console to clear, no one clipboard
/// to win, and no circuit to render. What each body needs it now takes from the cache and the
/// descriptor, and what it has to say it logs.
/// </remarks>
/// <param name="dashboard">Discovery, assessment, remediation, build, test and publish.</param>
/// <param name="cache">The estate as last discovered; the source of the row each item acts on.</param>
/// <param name="localRepo">Clones and the git operations performed directly on them.</param>
/// <param name="runtimeSettings">User settings, chiefly which repositories are excluded.</param>
/// <param name="fanOut">Turns a discovery's findings into one queued item per repository.</param>
/// <param name="gitHubTokens">
/// The signed-in user's GitHub token, handed forward by a circuit. Work runs with no request behind
/// it, so this — not the accessor — is where the token comes from.
/// </param>
/// <param name="httpContextAccessor">
/// A fallback source for the same token, for the case where an executor is somehow invoked on a
/// request thread. On the runner's own threads it always yields nothing.
/// </param>
/// <param name="logger">
/// Where the work narrates itself. The UI console mirrors this category, so a line logged here
/// reaches the console the item was queued from without the work knowing that console exists.
/// </param>
/// <param name="remediations">
/// Which rules can be fixed automatically. Dependabot triage asks this to tell a pull request an
/// existing remediation will handle from one that needs a remediation written.
/// </param>
/// <param name="triageRunner">
/// Carries out what triage decided. Injected rather than constructed per item so that many
/// repository lanes finding the same gap still produce one issue between them.
/// </param>
/// <param name="ollamaGate">
/// Bounds how many AI fixes are talking to the model at once. Shared, because the limit is a property
/// of the server and not of any one item.
/// </param>
/// <param name="playbooks">The per-rule instructions an AI fix is prompted with.</param>
/// <param name="sessionLogs">Where AI fix sessions are logged in full, or null to log nothing.</param>
/// <param name="governance">Writes what was done to the governance ledger, or null to record nothing.</param>
public sealed partial class WorkExecutors(
	DashboardService dashboard,
	DashboardCacheService cache,
	LocalRepoService localRepo,
	RuntimeSettingsService runtimeSettings,
	WorkFanOut fanOut,
	GitHubTokenProvider gitHubTokens,
	IHttpContextAccessor httpContextAccessor,
	ILogger<WorkExecutors> logger,
	Remediations.RemediationRegistry remediations,
	DependabotTriageRunner triageRunner,
	OllamaGate ollamaGate,
	AiPlaybookRegistry playbooks,
	AiSessionLogStore? sessionLogs = null,
	Governance.GovernanceRecorder? governance = null)
{
	/// <summary>
	/// Opens the on-disk log for one AI fix session and says where it is.
	/// </summary>
	/// <remarks>
	/// The transcript the pane shows is bounded and lost on restart; this is the record that survives, and
	/// is what a prompt or tool change is judged against. A store that is not configured yields a log that
	/// records nothing rather than a null every caller must check.
	/// </remarks>
	private AiSessionLog OpenSessionLog(string repositoryFullName, string workId)
	{
		if (sessionLogs is null)
		{
			return AiSessionLog.None;
		}

		var log = sessionLogs.Open(repositoryFullName, workId);

		if (log.Path.Length > 0)
		{
			Say($"📝 Session log: {log.Path}");
		}

		return log;
	}

	/// <summary>
	/// Writes what a session was given, which is the first record of its log and the one every later
	/// record has to be read against.
	/// </summary>
	private static void LogSessionStart(
		AiSessionLog log,
		AiFixRequest request,
		OllamaOptions ollama,
		AiFixOptions options,
		IReadOnlyList<string>? writablePaths)
		=> log.Write("session_start", new Dictionary<string, object?>
		{
			["repository"] = request.RepositoryFullName,
			["rule"] = request.RuleId,
			["rule_name"] = request.RuleName,
			["model"] = ollama.Model,
			["context_window"] = ollama.ContextWindow,
			["prompt_version"] = AiFixPrompt.Version,
			["tools"] = AiFixSession.ToolSpecs.Select(spec => spec.Name).ToList(),
			["max_turns_per_attempt"] = options.MaxTurnsPerAttempt,
			["max_attempts"] = options.MaxAttempts,
			["writable_paths"] = writablePaths,
			["system_prompt"] = request.SystemPrompt,
			["task"] = request.Task
		});

	/// <summary>Every kind this service knows how to run.</summary>
	/// <remarks>
	/// Exposed so a test can assert it covers <see cref="WorkKind"/> in full. A kind that can be
	/// queued but not run would sit in a lane for ever, blocking everything behind it.
	/// </remarks>
	public static IReadOnlySet<WorkKind> SupportedKinds { get; } = new HashSet<WorkKind>
	{
		WorkKind.Clone, WorkKind.Reassess, WorkKind.FixAll, WorkKind.FixCategory, WorkKind.FixRule,
		WorkKind.FixWithAiRule,
		WorkKind.AnalyseIssue, WorkKind.FixWithAiIssue, WorkKind.UnstickWithAi,
		WorkKind.TriageDependabot,
		WorkKind.Build, WorkKind.Test, WorkKind.GitSync, WorkKind.CommitAndPush, WorkKind.Publish,
		WorkKind.RediscoverOrganization, WorkKind.DiscoverReassessTargets,
		WorkKind.DiscoverCloneTargets, WorkKind.RefreshAll
	};

	/// <summary>Runs one queued item.</summary>
	/// <param name="item">The item to run; its <see cref="WorkItem.Descriptor"/> selects the body.</param>
	/// <param name="progress">Reports progress lines into the item's tree node.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	public async Task ExecuteAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		// Held for the duration of the run so Say can reach it. Safe as an instance field because
		// WorkRunnerService resolves a fresh scope — and therefore a fresh WorkExecutors — for every
		// item, so two lanes running together never share one.
		_running = item;

		try
		{
			await DispatchAsync(item, progress, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			// In a finally, and so also after a failure or a stop: work that was interrupted half way
			// through rewriting files has changed the tree just as surely as work that finished, and
			// the remembered build result describes neither.
			ForgetBuildStatusIfInvalidated(item);
		}
	}

	/// <summary>
	/// Throws away the repository's remembered build and test results when the work that just ran could have
	/// changed what is on disk.
	/// </summary>
	/// <param name="item">The item that ran.</param>
	private void ForgetBuildStatusIfInvalidated(WorkItem item)
	{
		if (!BuildStatusLifetime.Invalidates(item.Descriptor.Kind))
		{
			return;
		}

		var row = RowFor(item);
		if (row is null || !row.HasVerificationResults)
		{
			return;
		}

		row.ForgetVerificationResults();
		cache.UpsertRow(row);
	}

	private Task DispatchAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
		=> item.Descriptor.Kind switch
		{
			WorkKind.Clone => CloneAsync(item, progress, cancellationToken),
			WorkKind.Reassess => ReassessAsync(item, cancellationToken),
			WorkKind.FixAll => FixAllAsync(item, progress, cancellationToken),
			WorkKind.FixCategory => FixCategoryAsync(item, cancellationToken),
			WorkKind.FixRule => FixRuleAsync(item, progress, cancellationToken),
			WorkKind.FixWithAiRule => FixWithAiRuleAsync(item, progress, cancellationToken),
			WorkKind.AnalyseIssue => AnalyseIssueAsync(item, cancellationToken),
			WorkKind.FixWithAiIssue => FixWithAiIssueAsync(item, progress, cancellationToken),
			WorkKind.UnstickWithAi => UnstickWithAiAsync(item, progress, cancellationToken),
			WorkKind.TriageDependabot => TriageDependabotAsync(item, progress, cancellationToken),
			WorkKind.Build => BuildAsync(item, progress, cancellationToken),
			WorkKind.Test => TestAsync(item, cancellationToken),
			WorkKind.GitSync => GitSyncAsync(item, cancellationToken),
			WorkKind.CommitAndPush => CommitAndPushAsync(item, progress, cancellationToken),
			WorkKind.Publish => PublishAsync(item, progress, cancellationToken),
			WorkKind.RediscoverOrganization => RediscoverOrganizationAsync(item, cancellationToken),
			WorkKind.DiscoverReassessTargets => DiscoverReassessTargetsAsync(item, progress, cancellationToken),
			WorkKind.DiscoverCloneTargets => DiscoverCloneTargetsAsync(item, progress, cancellationToken),
			WorkKind.RefreshAll => RefreshAllAsync(item, progress, cancellationToken),
			_ => throw new NotSupportedException($"No executor for {item.Descriptor.Kind}.")
		};

	/// <summary>The item currently running, so <see cref="Say"/> can reach its transcript.</summary>
	private WorkItem? _running;

	/// <summary>
	/// Says something in the console the item was queued from, and in the item's own transcript.
	/// </summary>
	/// <remarks>
	/// Both, not either. The UI console subscribes to this logging category and answers "what is this
	/// application doing"; the transcript answers "what did this item do", which the console stops
	/// being able to answer the moment two lanes interleave into it. The line is passed as an argument
	/// rather than as the template so braces in a path or a git message are not read as
	/// structured-logging placeholders.
	/// </remarks>
	/// <param name="line">The line to say.</param>
	private void Say(string line)
	{
		_running?.Transcript.Append(WorkLineKind.Output, line);
		logger.LogInformation("{ConsoleLine}", line);
	}

	/// <summary>
	/// Notes that a restored item names a repository the estate no longer has, so its body is skipped.
	/// </summary>
	/// <param name="item">The item that cannot run.</param>
	private void SayRepositoryGone(WorkItem item)
		=> logger.LogWarning(
			"Skipping {Title}: {Repository} is no longer in the estate.",
			item.Title,
			item.RepositoryFullName);

	/// <summary>
	/// The cached row for an item's repository, or null when the repository is no longer known —
	/// which happens when a restored item names a repository since removed from the estate.
	/// </summary>
	/// <param name="item">The item whose repository is wanted.</param>
	private RepositoryDashboardRow? RowFor(WorkItem item)
		=> cache.GetCachedRows()?.FirstOrDefault(r => string.Equals(
			r.RepositoryFullName, item.RepositoryFullName, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Runs one repository's tests, and on failure leaves behind the AI prompt for the failure.
	/// </summary>
	/// <remarks>
	/// Like the build, the prompt is stored rather than pushed at a browser, and quotes only the
	/// lines this run produced.
	/// </remarks>
	/// <param name="item">The item naming the repository to test.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task TestAsync(WorkItem item, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var output = new List<string>();
		Say("▶ Running tests...");

		try
		{
			await dashboard.RunTestsAsync(
				row,
				line =>
				{
					output.Add(line);
					Say(line);
				},
				cancellationToken,
				sandbox: item.Descriptor.Parameter("sandbox") == "true", null).ConfigureAwait(false);

			var passed = row.Status == PackageStatus.TestsPassed;

			if (passed)
			{
				Say("✅ Tests passed");
				item.Succeeded = true;
			}
			else
			{
				Say("❌ Tests failed");
				item.GeneratedPrompt = DashboardService.GenerateConciseWorkflowFailurePrompt(row, "test", output);
				item.Succeeded = false;
			}

			RememberTestResult(row, passed ? RepositoryTestState.Passed : RepositoryTestState.Failed);
		}
		catch (OperationCanceledException)
		{
			// Stopping is not failing. Rethrown so the runner marks the item Cancelled and logs
			// the stop, and so the step badge is left alone rather than painted red for work the
			// user chose to end.
			throw;
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
			item.Succeeded = false;

			// Tests that could not be run are a repository whose tests do not pass here, which is what
			// the badge is asked. Leaving it as not-known would hide it among the never-tested.
			RememberTestResult(row, RepositoryTestState.Failed);
		}
	}

	/// <summary>
	/// A GitHub client carrying the GitHub App's installation token when an App is configured, otherwise
	/// the signed-in user's token when one can be read.
	/// </summary>
	/// <remarks>
	/// With an App configured the client acts as the App and spends its rate limit, not the user's; a
	/// broken App configuration throws here rather than falling back to the user's token.
	/// <para>
	/// Without an App, the token belongs to a sign-in, and work no longer belongs to the browser tab that started
	/// it: by the time a lane reaches an item there is no request in flight at all. The runner's
	/// asynchronous flow starts at the host rather than at a request, so
	/// <c>IHttpContextAccessor.HttpContext</c> is null here always, not merely sometimes — which is
	/// why the token is taken from <see cref="GitHubTokenProvider"/>, where a circuit published it
	/// while it still had a request to read it from. The accessor is kept only as a fallback for a
	/// caller that does happen to be on a request thread.
	/// </para>
	/// <para>
	/// With neither, the client is anonymous: it cannot see private repositories at all, and shares
	/// the 60-per-hour anonymous rate limit — which one fanned-out organisation re-assessment
	/// exhausts on its first pass. That is worth a warning rather than being passed off as normal.
	/// </para>
	/// </remarks>
	private async Task<IGitHubClient> CreateGitHubClientAsync()
	{
		var client = new GitHubClient(new ProductHeaderValue("PanoramicData.NugetManagement.Web"));

		var accessToken = await gitHubTokens.GetWorkTokenAsync(CancellationToken.None).ConfigureAwait(false);

		if (string.IsNullOrWhiteSpace(accessToken) && !gitHubTokens.UsesGitHubApp && httpContextAccessor.HttpContext is { } httpContext)
		{
			accessToken = await httpContext.GetTokenAsync("access_token").ConfigureAwait(false);
		}

		if (string.IsNullOrWhiteSpace(accessToken))
		{
			logger.LogWarning(
				"⚠️ No signed-in GitHub token is available, so this assessment will run anonymously: "
				+ "private repositories cannot be read at all, and public ones share the 60-requests-per-hour "
				+ "anonymous rate limit. Sign in and open the dashboard once to give the work runner a token.");
			return client;
		}

		client.Credentials = new Credentials(accessToken);
		return client;
	}
}
