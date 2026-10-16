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

	/// <summary>
	/// How long the whole session may run, or null for no limit.
	/// </summary>
	/// <remarks>
	/// Turns bound how much a model may do and not how long it may take, and the two come apart: a
	/// session that writes whole test files spends most of a minute on a single turn. Without a clock one
	/// stubborn repository can hold the only model for an hour while the rest of the estate waits.
	/// </remarks>
	public TimeSpan? TimeBudget { get; init; }
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
	AiSessionLog? sessionLog = null,
	Action<string>? onStatus = null,
	Func<string?>? statusDetail = null)
{
	private readonly AiSessionLog _log = sessionLog ?? AiSessionLog.None;

	private readonly Stopwatch _clock = new();

	private bool OutOfTime => options.TimeBudget is { } budget && _clock.Elapsed > budget;

	private int _attempt = 1;

	private int _turn;

	private string? _lastEvent;

	/// <summary>
	/// Says where the session has got to, in one line, for the work pane and the tree.
	/// </summary>
	/// <param name="activity">What it is doing right now.</param>
	/// <remarks>
	/// The pane is a scrolling log, which answers "what happened" and not "where are we". This is the second
	/// question: which attempt and turn, how much of the clock is spent, whatever the caller wants shown
	/// alongside (the coverage figure, for one), and the last thing that came back.
	/// </remarks>
	private void ReportStatus(string activity)
	{
		if (onStatus is null)
		{
			return;
		}

		var minutes = _clock.Elapsed.TotalMinutes.ToString("0", CultureInfo.InvariantCulture);

		var parts = new List<string>
		{
			$"attempt {_attempt.ToString(CultureInfo.InvariantCulture)}/{options.MaxAttempts.ToString(CultureInfo.InvariantCulture)}",
			$"turn {_turn.ToString(CultureInfo.InvariantCulture)}/{options.MaxTurnsPerAttempt.ToString(CultureInfo.InvariantCulture)}",
			options.TimeBudget is { } budget
				? $"{minutes} of {budget.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} min"
				: $"{minutes} min"
		};

		if (statusDetail?.Invoke() is { Length: > 0 } detail)
		{
			parts.Add(detail);
		}

		if (_lastEvent is { Length: > 0 })
		{
			parts.Add($"last: {_lastEvent}");
		}

		parts.Add(activity);

		onStatus(string.Join(" · ", parts));
	}

	private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";

	/// <summary>
	/// The tools this session can actually run. A tool described to the model that the toolbox cannot
	/// execute costs a turn to find out, so what is described follows what is offered.
	/// </summary>
	private readonly IReadOnlyList<AiToolSpec> _tools = [.. ToolSpecs.Where(spec => toolbox.Offers(spec.Name))];

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
			"run_coverage",
			"Run the tests with coverage and return the line coverage figure, whether the tests passed, and the "
			+ "files with the most lines that never run, with their line numbers. Use it to see where tests are missing.",
			new Dictionary<string, string>(),
			[]),
		new(
			"restore_file",
			"Put one file back exactly as it is in the last commit, undoing every uncommitted change to it. "
			+ "Use it when a changed file cannot be made to work and the change is not worth keeping.",
			new Dictionary<string, string>
			{
				["path"] = "Path relative to the repository root."
			},
			["path"]),
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

		_clock.Restart();

		for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (OutOfTime)
			{
				onOutput($"⏱ {request.RuleId}: the {options.TimeBudget!.Value.TotalMinutes:0}-minute budget is spent, so no further attempt is made.");
				break;
			}

			onOutput($"🤖 {request.RuleId} on {request.RepositoryFullName}: attempt "
				+ $"{attempt.ToString(CultureInfo.InvariantCulture)} of "
				+ $"{options.MaxAttempts.ToString(CultureInfo.InvariantCulture)}.");

			_log.Write("attempt_start", new Dictionary<string, object?> { ["attempt"] = attempt });

			_attempt = attempt;

			var finishedByModel = await RunAttemptAsync(conversation, request, attempt, cancellationToken)
				.ConfigureAwait(false);

			ReportStatus("checking whether that fixed it");
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

			if (OutOfTime)
			{
				onOutput("   ⏱ Time budget spent; checking the rule with what has been done.");
				return false;
			}

			var turnNumber = turn + 1;
			_turn = turnNumber;
			ReportStatus("waiting for the model");
			var turnTimer = Stopwatch.StartNew();

			AiModelTurn next;

			try
			{
				next = await model
					.NextAsync(request.SystemPrompt, conversation, _tools, onDelta, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (AiModelOutputException ex)
			{
				// The model's own garbled tool call. Worth one turn and a plain telling-off, not the session:
				// the same call usually comes out right the second time once it knows it came out wrong.
				turnTimer.Stop();

				onOutput($"   ⚠️ Turn {turnNumber.ToString(CultureInfo.InvariantCulture)}: the model's tool call could not be read ({Summarise(ex.Message)}).");

				_log.Write("model_error", new Dictionary<string, object?>
				{
					["attempt"] = attempt,
					["turn"] = turnNumber,
					["message"] = ex.Message,
					["wall_seconds"] = Math.Round(turnTimer.Elapsed.TotalSeconds, 1)
				});

				conversation.Add(new AiMessage(
					"user",
					"Your last tool call was malformed and could not be read. Make it again. Keep every argument "
					+ "plain text, and do not put XML or tool-call markup inside file contents."));

				continue;
			}

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

				// With thinking off there is no reasoning to watch, so what the model is doing has to be shown
				// instead: the text it is putting in, which says more than a file name does.
				if (CallDetail(call) is { Length: > 0 } detail)
				{
					onDelta?.Invoke(new AiStreamDelta(AiDeltaKind.Content, detail + "\n"));
				}

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

				_lastEvent = $"{call.Name}{DescribeArguments(call)}: {Truncate(Summarise(result.Content), 70)}";
				ReportStatus("waiting for the model");

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
	/// <summary>
	/// The text a write puts into a file, shortened, for the pane. Null for a call with nothing worth showing.
	/// </summary>
	/// <param name="call">The tool call.</param>
	internal static string? CallDetail(AiToolCall call)
	{
		static string Cut(string text) => text.Length <= 400 ? text : text[..400] + $"… ({text.Length.ToString(CultureInfo.InvariantCulture)} characters)";

		return call.Name switch
		{
			"replace_in_file" when call.Arguments.TryGetValue("old_text", out var old) && call.Arguments.TryGetValue("new_text", out var replacement)
				=> $"replace in {call.Arguments.GetValueOrDefault("path")}:\n- {Cut(old).Replace("\n", "\n- ", StringComparison.Ordinal)}\n+ {Cut(replacement).Replace("\n", "\n+ ", StringComparison.Ordinal)}",
			"write_file" when call.Arguments.TryGetValue("content", out var content)
				=> $"write {call.Arguments.GetValueOrDefault("path")}:\n{Cut(content)}",
			"finish" when call.Arguments.TryGetValue("summary", out var summary)
				=> $"finish: {Cut(summary)}",
			_ => null
		};
	}

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
