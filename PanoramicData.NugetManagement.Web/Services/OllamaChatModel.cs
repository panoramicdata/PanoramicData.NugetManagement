using Ollama.Api;
using Ollama.Api.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// <see cref="IChatModel"/> backed by an Ollama server, via the Ollama.Api client.
/// </summary>
/// <remarks>
/// Translation only, as <c>OctokitGitHubIssueApi</c> is. Nothing here decides anything: the loop, the
/// tools and the prompt all live behind <see cref="IChatModel"/> so they can be tested without a GPU,
/// and this is the thin piece that cannot be.
/// <para>
/// Native tool calling rather than a text protocol. Ollama.Api models it directly and qwen3 was trained
/// on it, so there is no parser of ours to get wrong; a model that emits nothing usable is handled by
/// the loop, which tells it to call a tool and moves on.
/// </para>
/// </remarks>
public sealed class OllamaChatModel(OllamaClient client, string model, int? contextWindow) : IChatModel
{
	/// <inheritdoc />
	public async Task<AiModelTurn> NextAsync(
		string systemPrompt,
		IReadOnlyList<AiMessage> conversation,
		IReadOnlyList<AiToolSpec> tools,
		CancellationToken cancellationToken)
	{
		var messages = new List<ChatMessage>
		{
			new() { Role = "system", Content = systemPrompt }
		};

		messages.AddRange(conversation.Select(m => new ChatMessage
		{
			Role = m.Role,
			Content = m.Content
		}));

		var request = new ChatRequest
		{
			Model = model,
			Messages = messages,
			Stream = false,
			Tools = [.. tools.Select(Translate)],

			// The window is stated rather than left to the server's default, which is commonly far smaller
			// than the model supports — a truncated conversation looks exactly like a model that has
			// forgotten what it was asked.
			Options = contextWindow is null ? null : new GenerateOptions { NumCtx = contextWindow }
		};

		var response = await client.Chat.ChatAsync(request, cancellationToken).ConfigureAwait(false);

		if (response.Error is { Length: > 0 } error)
		{
			throw new InvalidOperationException($"Ollama reported: {error}");
		}

		var calls = response.Message?.ToolCalls?
			.Select(Translate)
			.ToList() ?? [];

		return new AiModelTurn(response.Message?.Content, calls);
	}

	private static ChatTool Translate(AiToolSpec spec) => new()
	{
		Type = McpType.Function,
		Function = new ChatToolFunction
		{
			Name = spec.Name,
			Description = spec.Description,
			Parameters = new ChatToolFunctionParameters
			{
				Type = McpType.Object,
				Properties = spec.Parameters.ToDictionary(
					p => p.Key,
					p => new ChatToolFunctionInputSchemaProperty
					{
						// Every argument is a string. A small model asked for an integer often sends "3"
						// anyway, and rejecting that as a schema violation loses a turn to a distinction
						// that does not matter to any of these tools.
						Type = McpType.String,
						Description = p.Value
					}),
				Required = [.. spec.Required]
			}
		}
	};

	/// <summary>
	/// One tool call, with every argument reduced to a string.
	/// </summary>
	/// <remarks>
	/// Ollama hands arguments back as loosely-typed objects because that is what the model produced. The
	/// toolbox wants strings, and a number or a boolean that arrives as one is still the right value —
	/// so it is converted rather than refused.
	/// </remarks>
	private static AiToolCall Translate(ChatToolCall call)
		=> new(
			call.Function.Name,
			call.Function.Arguments.ToDictionary(
				a => a.Key,
				a => a.Value?.ToString() ?? string.Empty,
				StringComparer.Ordinal));
}
