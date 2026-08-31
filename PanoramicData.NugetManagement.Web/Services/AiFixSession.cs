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
	Action<string> onOutput)
{
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
			"write_file",
			"Replace one file's entire contents. Include the whole file, not just the part you changed.",
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

			await RunAttemptAsync(conversation, request, cancellationToken).ConfigureAwait(false);

			var check = await checkRuleAsync(cancellationToken).ConfigureAwait(false);

			if (check.Passed)
			{
				onOutput($"✅ {request.RuleId} now passes: {check.Message}");

				return new AiFixOutcome(
					true,
					attempt,
					$"{request.RuleName} fixed on attempt {attempt.ToString(CultureInfo.InvariantCulture)}.");
			}

			onOutput($"↻ {request.RuleId} still fails: {check.Message}");

			// The rule's own message, verbatim. It is the most precise correction available and it is
			// already written for whoever has to act on it.
			conversation.Add(new AiMessage(
				"user",
				$"That did not fix it. The rule still reports: {check.Message}\n\n"
				+ "Look again, and make the change that would satisfy it."));
		}

		onOutput($"❌ {request.RuleId} not fixed after "
			+ $"{options.MaxAttempts.ToString(CultureInfo.InvariantCulture)} attempt(s).");

		return new AiFixOutcome(
			false,
			options.MaxAttempts,
			$"{request.RuleName} was not fixed.");
	}

	/// <summary>
	/// One attempt: turns of tool calls until the model finishes or the turn limit is reached.
	/// </summary>
	private async Task RunAttemptAsync(
		List<AiMessage> conversation,
		AiFixRequest request,
		CancellationToken cancellationToken)
	{
		for (var turn = 0; turn < options.MaxTurnsPerAttempt; turn++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var next = await model
				.NextAsync(request.SystemPrompt, conversation, ToolSpecs, cancellationToken)
				.ConfigureAwait(false);

			if (next.ToolCalls.Count == 0)
			{
				// Prose is not progress. Said plainly rather than treated as an answer, because a small
				// model that has started explaining itself will keep explaining itself.
				conversation.Add(new AiMessage("assistant", next.Text ?? string.Empty));
				conversation.Add(new AiMessage(
					"user",
					"Do not explain. Call a tool. Use write_file to make the change, then call finish."));

				continue;
			}

			var finished = false;

			foreach (var call in next.ToolCalls)
			{
				var result = await toolbox.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);

				onOutput($"   {(result.IsError ? "⚠️" : "→")} {call.Name}{DescribeArguments(call)}: {Summarise(result.Content)}");

				conversation.Add(new AiMessage("assistant", $"Calling {call.Name}."));
				conversation.Add(new AiMessage("tool", result.Content));

				if (result.IsFinish)
				{
					finished = true;
				}
			}

			if (finished)
			{
				return;
			}
		}

		onOutput($"   ⚠️ Turn limit reached without finishing; checking the rule anyway.");
	}

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
