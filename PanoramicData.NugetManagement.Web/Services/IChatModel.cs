namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// One message in the conversation sent to the model.
/// </summary>
/// <param name="Role">Whose turn it was: <c>user</c>, <c>assistant</c> or <c>tool</c>.</param>
/// <param name="Content">What was said.</param>
public sealed record AiMessage(string Role, string Content);

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
public sealed record AiModelTurn(string? Text, IReadOnlyList<AiToolCall> ToolCalls);

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
	/// <param name="cancellationToken">A cancellation token.</param>
	Task<AiModelTurn> NextAsync(
		string systemPrompt,
		IReadOnlyList<AiMessage> conversation,
		IReadOnlyList<AiToolSpec> tools,
		CancellationToken cancellationToken);
}
