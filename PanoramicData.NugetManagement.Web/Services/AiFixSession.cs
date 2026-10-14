using System.Diagnostics;
using System.Globalization;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// How hard an AI fix may try.
/// </summary>
/// <remarks>
/// Both limits exist to stop a model that has misunderstood the task from running indefinitely on a
/// shared box. The turn limit bounds one attempt; the attempt limit bounds the retries after the rule
/// disagrees with the model about being finished.
/// </remarks>
public sealed class AiFixOptions
{
	/// <summary>Tool-calling turns allowed in a single attempt.</summary>
	public int MaxTurnsPerAttempt { get; init; } = 12;

	/// <summary>Attempts allowed before giving up on the rule.</summary>
	public int MaxAttempts { get; init; } = 3;
}

/// <summary>
/// What one AI fix is being asked to do.
/// </summary>
/// <param name="RepositoryFullName">The repository, as "owner/name".</param>
/// <param name="RuleId">The rule to satisfy.</param>
/// <param name="RuleName">Its human name, for the log.</param>
/// <param name="Task">The prompt describing the work: playbook and instance data.</param>
/// <param name="SystemPrompt">The fixed instructions.</param>
public sealed record AiFixRequest(
	string RepositoryFullName,
	string RuleId,
	string RuleName,
	string Task,
	string SystemPrompt);

/// <summary>
/// Whether the rule now passes, and what it said.
/// </summary>
/// <param name="Passed">Whether the rule is satisfied.</param>
/// <param name="Message">The rule's own message, which is the correction fed back on failure.</param>
public sealed record AiRuleCheck(bool Passed, string Message);

/// <summary>
/// What an AI fix achieved.
/// </summary>
/// <param name="Succeeded">Whether the rule ended up passing.</param>
/// <param name="Attempts">How many attempts it took, or were spent failing.</param>
/// <param name="Summary">A line for the work item's output.</param>
public sealed record AiFixOutcome(bool Succeeded, int Attempts, string Summary);

/// <summary>
/// Runs a model against one rule in one repository until the rule agrees it is fixed.
/// </summary>
/// <remarks>
/// The rule is the specification, and re-evaluating it costs nothing, so the model is never trusted
/// about being finished — it is checked. That loop is what makes a 27b model useful here: it does not
/// have to be right first time, only right eventually, and it is told exactly how it was wrong.
/// </remarks>
public sealed class AiFixSession(
	IChatModel model,
	AiFixToolbox toolbox,
	AiFixOptions options,
	Action<string> onOutput,
	Action<AiStreamDelta>? onDelta = null,
	AiSessionLog? sessionLog = null)
{
	private readonly AiSessionLog _log = sessionLog ?? AiSessionLog.None;

	/// <summary>
	/// The tools described to the model, in the order they are most likely to be needed.
	/// </summary>
	/// <remarks>
	/// Deliberately the same set <see cref="AiFixToolbox"/> executes, and a test holds the two together:
	/// a tool described but not executed wastes a turn, and one executed but not described will never be
	/// called.
	/// </remarks>
	public static IReadOnlyList<AiToolSpec> ToolSpecs { get; } =
	[
		new(
			"list_files",
			"List the repository's files. Call this first if you do not know what is there.",
			new Dictionary<string, string>
			{
				["glob"] = "Optional filename pattern, for example *.csproj. Omit for every file."
			},
			[]),
		new(
			"read_file",
			"Read one file. Always read a file before you rewrite it.",
			new Dictionary<string, string>
			{
				["path"] = "Path relative to the repository root, for example src/Sample.csproj."
			},
			["path"]),
		new(
			"replace_in_file",
			"Change part of an existing file. Prefer this to write_file: you send only the piece that changes. "
			+ "old_text must match the file exactly once, including whitespace.",
			new Dictionary<string, string>
			{
				["path"] = "Path relative to the repository root.",
				["old_text"] = "The exact existing text to replace. Include enough lines to be unique.",
				["new_text"] = "The text to put in its place."
			},
			["path", "old_text", "new_text"]),
		new(
			"write_file",
			"Replace one file's entire contents, or create a new file. Include the whole file, not just the "
			+ "part you changed. Use replace_in_file instead for a change to an existing file.",
			new Dictionary<string, string>
			{
				["path"] = "Path relative to the repository root. Missing folders are created.",
				["content"] = "The file's complete new text."
			},
			["path", "content"]),
		new(
			"run_build",
			"Build the repository and return the output. Use this to check that your change compiles.",
			new Dictionary<string, string>(),
			[]),
		new(
			"run_tests",
			"Run the repository's tests and return the output.",
			new Dictionary<string, string>(),
			[]),
		new(
			"finish",
			"Call this when the task is complete. Do not call it before you have made the change.",
			new Dictionary<string, string>
			{
				["summary"] = "One line saying what you changed."
			},
			["summary"])
	];

	/// <summary>
	/// Runs the fix.
	/// </summary>
	/// <param name="request">What to do.</param>
	/// <param name="checkRuleAsync">Re-evaluates the rule against the clone as it now stands.</param>
	/// <param name="cancellationToken">Signalled when the user stops the work item.</param>
	public async Task<AiFixOutcome> RunAsync(
		AiFixRequest request,
		Func<CancellationToken, Task<AiRuleCheck>> checkRuleAsync,
		CancellationToken cancellationToken)
	{
		var conversation = new List<AiMessage> { new("user", request.Task) };

		for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			onOutput($"🤖 {request.RuleId} on {request.RepositoryFullName}: attempt "
				+ $"{attempt.ToString(CultureInfo.InvariantCulture)} of "
				+ $"{options.MaxAttempts.ToString(CultureInfo.InvariantCulture)}.");

			_log.Write("attempt_start", new Dictionary<string, object?> { ["attempt"] = attempt });

			var finishedByModel = await RunAttemptAsync(conversation, request, attempt, cancellationToken)
				.ConfigureAwait(false);

			var checkTimer = Stopwatch.StartNew();
			var check = await checkRuleAsync(cancellationToken).ConfigureAwait(false);
			checkTimer.Stop();

			_log.Write("rule_check", new Dictionary<string, object?>
			{
				["attempt"] = attempt,
				["passed"] = check.Passed,
				["message"] = check.Message,
				["seconds"] = Math.Round(checkTimer.Elapsed.TotalSeconds, 1),
				["finished_by_model"] = finishedByModel,
				["files_written"] = toolbox.FilesWritten.Order(StringComparer.OrdinalIgnoreCase).ToList()
			});

			if (check.Passed)
			{
				onOutput($"✅ {request.RuleId} now passes: {check.Message} (re-check {Seconds(checkTimer.Elapsed)})");

				return Ended(
					new AiFixOutcome(
						true,
						attempt,
						$"{request.RuleName} fixed on attempt {attempt.ToString(CultureInfo.InvariantCulture)}."));
			}

			onOutput($"↻ {request.RuleId} still fails: {check.Message} (re-check {Seconds(checkTimer.Elapsed)})");

			// The rule's own message, verbatim. It is the most precise correction available and it is
			// already written for whoever has to act on it. Said alongside what has already been changed,
			// because a model whose attempt ended mid-way has no other way to know what state the files
			// are in.
			var changed = toolbox.FilesWritten.Count == 0
				? "You have not changed any file yet."
				: $"Files you have changed so far: {string.Join(", ", toolbox.FilesWritten.Order(StringComparer.OrdinalIgnoreCase))}.";

			conversation.Add(new AiMessage(
				"user",
				$"That did not fix it. The rule still reports: {check.Message}\n\n{changed}\n\n"
				+ "Read the files again if you need to, then make the change that would satisfy the rule."));
		}

		onOutput($"❌ {request.RuleId} not fixed after "
			+ $"{options.MaxAttempts.ToString(CultureInfo.InvariantCulture)} attempt(s).");

		return Ended(new AiFixOutcome(
			false,
			options.MaxAttempts,
			$"{request.RuleName} was not fixed."));
	}

	/// <summary>Records how the session ended, and hands the outcome back.</summary>
	private AiFixOutcome Ended(AiFixOutcome outcome)
	{
		_log.Write("session_end", new Dictionary<string, object?>
		{
			["succeeded"] = outcome.Succeeded,
			["attempts"] = outcome.Attempts,
			["summary"] = outcome.Summary,
			["files_written"] = toolbox.FilesWritten.Order(StringComparer.OrdinalIgnoreCase).ToList()
		});

		return outcome;
	}

	/// <summary>
	/// One attempt: turns of tool calls until the model finishes or the turn limit is reached.
	/// </summary>
	/// <returns>Whether the model called <c>finish</c>, as opposed to running out of turns.</returns>
	private async Task<bool> RunAttemptAsync(
		List<AiMessage> conversation,
		AiFixRequest request,
		int attempt,
		CancellationToken cancellationToken)
	{
		for (var turn = 0; turn < options.MaxTurnsPerAttempt; turn++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var turnNumber = turn + 1;
			var turnTimer = Stopwatch.StartNew();

			var next = await model
				.NextAsync(request.SystemPrompt, conversation, ToolSpecs, onDelta, cancellationToken)
				.ConfigureAwait(false);

			turnTimer.Stop();

			onOutput(DescribeTurn(turnNumber, next.Metrics, turnTimer.Elapsed));

			_log.Write("turn", new Dictionary<string, object?>
			{
				["attempt"] = attempt,
				["turn"] = turnNumber,
				["thinking"] = next.Thinking,
				["text"] = next.Text,
				["tool_calls"] = next.ToolCalls
					.Select(call => new Dictionary<string, object?> { ["name"] = call.Name, ["arguments"] = call.Arguments })
					.ToList(),
				["wall_seconds"] = Math.Round(turnTimer.Elapsed.TotalSeconds, 1),
				["metrics"] = next.Metrics is { } m
					? new Dictionary<string, object?>
					{
						["prompt_tokens"] = m.PromptTokens,
						["prompt_seconds"] = Math.Round(m.PromptTime.TotalSeconds, 2),
						["generated_tokens"] = m.GeneratedTokens,
						["generated_seconds"] = Math.Round(m.GeneratedTime.TotalSeconds, 2),
						["load_seconds"] = Math.Round(m.LoadTime.TotalSeconds, 2),
						["total_seconds"] = Math.Round(m.TotalTime.TotalSeconds, 2),
						["waited_seconds"] = Math.Round(m.Waited.TotalSeconds, 2),
						["tokens_per_second"] = Math.Round(m.TokensPerSecond, 1)
					}
					: null
			});

			if (next.ToolCalls.Count == 0)
			{
				// Prose is not progress. Said plainly rather than treated as an answer, because a small
				// model that has started explaining itself will keep explaining itself.
				conversation.Add(new AiMessage("assistant", next.Text ?? string.Empty));
				conversation.Add(new AiMessage(
					"user",
					"Do not explain. Call a tool. Use replace_in_file or write_file to make the change, then call finish."));

				continue;
			}

			// The calls themselves, with their arguments, and not a sentence about them. Whatever the model
			// said alongside them is dropped from the history: it was narration, and repeating it back
			// teaches the next turn to narrate.
			conversation.Add(new AiMessage("assistant", string.Empty, next.ToolCalls));

			var finished = false;

			foreach (var call in next.ToolCalls)
			{
				var toolTimer = Stopwatch.StartNew();
				var result = await toolbox.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
				toolTimer.Stop();

				onOutput($"   {(result.IsError ? "⚠️" : "→")} {call.Name}{DescribeArguments(call)}: "
					+ $"{Summarise(result.Content)} ({Seconds(toolTimer.Elapsed)})");

				_log.Write("tool_result", new Dictionary<string, object?>
				{
					["attempt"] = attempt,
					["turn"] = turnNumber,
					["name"] = call.Name,
					["is_error"] = result.IsError,
					["seconds"] = Math.Round(toolTimer.Elapsed.TotalSeconds, 2),
					["content"] = result.Content
				});

				conversation.Add(new AiMessage("tool", result.Content));

				if (result.IsFinish)
				{
					finished = true;
				}
			}

			if (finished)
			{
				return true;
			}
		}

		onOutput($"   ⚠️ Turn limit reached without finishing; checking the rule anyway.");

		return false;
	}

	/// <summary>
	/// One line saying what a turn cost: how long it queued, and how fast the model worked once it was
	/// being served.
	/// </summary>
	private static string DescribeTurn(int turn, AiTurnMetrics? metrics, TimeSpan wall)
		=> metrics is null
			? $"⏱ turn {turn.ToString(CultureInfo.InvariantCulture)}: {Seconds(wall)}"
			: $"⏱ turn {turn.ToString(CultureInfo.InvariantCulture)}: waited {Seconds(metrics.Waited)} · "
				+ $"prompt {metrics.PromptTokens.ToString("N0", CultureInfo.InvariantCulture)} tok in {Seconds(metrics.PromptTime)} · "
				+ $"generated {metrics.GeneratedTokens.ToString("N0", CultureInfo.InvariantCulture)} tok in "
				+ $"{Seconds(metrics.GeneratedTime)} ({metrics.TokensPerSecond.ToString("N0", CultureInfo.InvariantCulture)} tok/s)";

	private static string Seconds(TimeSpan span)
		=> $"{span.TotalSeconds.ToString("N1", CultureInfo.InvariantCulture)}s";

	/// <summary>The arguments worth showing in the log: the path, where there is one.</summary>
	private static string DescribeArguments(AiToolCall call)
		=> call.Arguments.TryGetValue("path", out var path) ? $" {path}" : string.Empty;

	/// <summary>
	/// One line of a tool result for the log. The full result goes to the model, not to the reader.
	/// </summary>
	private static string Summarise(string content)
	{
		var firstLine = content.Split('\n')[0].Trim();

		return firstLine.Length <= 160 ? firstLine : firstLine[..160] + "…";
	}
}
