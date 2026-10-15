namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// One message in the conversation sent to the model.
/// </summary>
/// <param name="Role">Whose turn it was: <c>user</c>, <c>assistant</c> or <c>tool</c>.</param>
/// <param name="Content">What was said.</param>
/// <param name="ToolCalls">
/// The calls an assistant message made, with their arguments. Kept so the model is shown its own
/// earlier calls as calls: a history that says only "Calling write_file." leaves it unable to see what
/// it wrote, and a model that cannot see its own write re-reads and re-writes the same file.
/// </param>
public sealed record AiMessage(string Role, string Content, IReadOnlyList<AiToolCall>? ToolCalls = null);

/// <summary>
/// A tool as described to the model.
/// </summary>
/// <param name="Name">The tool's name, matching what <see cref="AiFixToolbox"/> executes.</param>
/// <param name="Description">What it does, in a sentence the model will act on.</param>
/// <param name="Parameters">Each argument's name and description, all treated as strings.</param>
/// <param name="Required">Which arguments must be supplied.</param>
/// <remarks>
/// Every argument is a string on purpose. A weak model asked for an integer or a boolean will often
/// send <c>"3"</c> or <c>"true"</c> anyway, and rejecting that as a schema violation loses a turn to a
/// distinction that does not matter here.
/// </remarks>
public sealed record AiToolSpec(
	string Name,
	string Description,
	IReadOnlyDictionary<string, string> Parameters,
	IReadOnlyList<string> Required);

/// <summary>
/// What the model produced for one turn.
/// </summary>
/// <param name="Text">Anything it said, which is usually ignorable.</param>
/// <param name="ToolCalls">What it wants to do; empty when it only talked.</param>
/// <param name="Thinking">Its reasoning, when the model separates that from its answer.</param>
/// <param name="Metrics">What the server reported about the turn's cost, when it reports any.</param>
public sealed record AiModelTurn(
	string? Text,
	IReadOnlyList<AiToolCall> ToolCalls,
	string? Thinking = null,
	AiTurnMetrics? Metrics = null);

/// <summary>
/// What one turn cost, as the server measured it.
/// </summary>
/// <param name="PromptTokens">Tokens the server evaluated from the conversation.</param>
/// <param name="PromptTime">Time spent evaluating them.</param>
/// <param name="GeneratedTokens">Tokens the model produced.</param>
/// <param name="GeneratedTime">Time spent producing them.</param>
/// <param name="LoadTime">Time spent loading the model, nonzero only on a cold start.</param>
/// <param name="TotalTime">The server's own total for the request, queueing included.</param>
/// <remarks>
/// <see cref="Waited"/> is the figure that was missing: time queued behind another caller on the same
/// model, which once accounted for 308 of a turn's 311 seconds and appeared nowhere.
/// </remarks>
public sealed record AiTurnMetrics(
	int PromptTokens,
	TimeSpan PromptTime,
	int GeneratedTokens,
	TimeSpan GeneratedTime,
	TimeSpan LoadTime,
	TimeSpan TotalTime)
{
	/// <summary>Time the request spent waiting rather than being worked on.</summary>
	public TimeSpan Waited
	{
		get
		{
			var waited = TotalTime - LoadTime - PromptTime - GeneratedTime;

			return waited < TimeSpan.Zero ? TimeSpan.Zero : waited;
		}
	}

	/// <summary>Generation speed, or zero when nothing was generated.</summary>
	public double TokensPerSecond
		=> GeneratedTime > TimeSpan.Zero ? GeneratedTokens / GeneratedTime.TotalSeconds : 0;
}

/// <summary>
/// The model produced something the server could not read, as opposed to the server being unreachable
/// or misconfigured.
/// </summary>
/// <remarks>
/// A small model sometimes emits a tool call that is not well formed — Ollama's own parser then reports
/// an XML syntax error and sends no call at all. That is the model's mistake and the model can be told
/// about it, which is worth one turn. It is kept apart from every other failure so that the loop
/// retries this and nothing else: retrying a missing model or a refused connection would only spend the
/// whole turn budget discovering the same thing twelve times.
/// </remarks>
public sealed class AiModelOutputException(string message) : Exception(message);

/// <summary>
/// The model, reduced to the one question this application asks of it.
/// </summary>
/// <remarks>
/// A port, so the loop, the tools and the prompt can all be tested against a scripted model rather
/// than a GPU. The same seam as <c>IGitHubIssueApi</c>: the interesting behaviour is ours, and none of
/// it should need a server to exercise.
/// </remarks>
public interface IChatModel
{
	/// <summary>
	/// The model's next turn.
	/// </summary>
	/// <param name="systemPrompt">The fixed instructions.</param>
	/// <param name="conversation">Everything said so far.</param>
	/// <param name="tools">The tools it may call.</param>
	/// <param name="onDelta">
	/// Called as each fragment arrives, or null to be told nothing until the turn is complete. The
	/// return value is the same either way: this is a window onto the turn, not a different turn.
	/// </param>
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<AiModelTurn> NextAsync(
		string systemPrompt,
		IReadOnlyList<AiMessage> conversation,
		IReadOnlyList<AiToolSpec> tools,
		Action<AiStreamDelta>? onDelta,
		CancellationToken cancellationToken);
}

/// <summary>
/// Which channel a streamed fragment arrived on.
/// </summary>
public enum AiDeltaKind
{
	/// <summary>The model's reasoning, from Ollama's <c>thinking</c> field.</summary>
	Thinking,

	/// <summary>What the model is saying, from the message content.</summary>
	Content
}

/// <summary>
/// One fragment of a turn, as it arrives.
/// </summary>
/// <param name="Kind">Which channel it came from.</param>
/// <param name="Text">The fragment, often a single token.</param>
/// <remarks>
/// A 27b model grinding through an agentic loop takes minutes per attempt. Without this the pane can
/// only show a session once it is over, which is most of the reason for wanting to watch one.
/// </remarks>
public sealed record AiStreamDelta(AiDeltaKind Kind, string Text);
