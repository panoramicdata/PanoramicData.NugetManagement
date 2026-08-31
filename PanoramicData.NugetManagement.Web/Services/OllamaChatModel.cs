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
		Action<AiStreamDelta>? onDelta,
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

		return onDelta is null
			? await CompleteAsync(request, cancellationToken).ConfigureAwait(false)
			: await StreamAsync(request, onDelta, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// One turn, waited for in full.
	/// </summary>
	private async Task<AiModelTurn> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
	{
		var response = await client.Chat.ChatAsync(request, cancellationToken).ConfigureAwait(false);

		Throw(response.Error);

		return new AiModelTurn(response.Message?.Content, Calls(response.Message));
	}

	/// <summary>
	/// One turn, reported fragment by fragment as it arrives and assembled into the same result.
	/// </summary>
	/// <remarks>
	/// The two channels are kept apart all the way through. Ollama sends a thinking model's reasoning
	/// on <c>thinking</c> and its answer on <c>content</c>, and gluing them together here would leave
	/// the pane no way to tell a long think from a long answer.
	/// <para>
	/// Tool calls are collected from whichever chunks carry them rather than from the last one: Ollama
	/// emits them on the chunk that completes them, which is not necessarily the chunk that ends the
	/// turn.
	/// </para>
	/// </remarks>
	private async Task<AiModelTurn> StreamAsync(
		ChatRequest request,
		Action<AiStreamDelta> onDelta,
		CancellationToken cancellationToken)
	{
		var content = new System.Text.StringBuilder();
		var calls = new List<AiToolCall>();

		await foreach (var chunk in client.Chat.ChatStreamAsync(request, cancellationToken).ConfigureAwait(false))
		{
			Throw(chunk.Error);

			if (chunk.Message is not { } message)
			{
				continue;
			}

			if (message.Thinking is { Length: > 0 } thinking)
			{
				onDelta(new AiStreamDelta(AiDeltaKind.Thinking, thinking));
			}

			if (message.Content is { Length: > 0 } fragment)
			{
				content.Append(fragment);
				onDelta(new AiStreamDelta(AiDeltaKind.Content, fragment));
			}

			calls.AddRange(Calls(message));
		}

		return new AiModelTurn(content.Length == 0 ? null : content.ToString(), calls);
	}

	/// <summary>Fails the turn on an error the server reported, which is not an HTTP failure.</summary>
	private static void Throw(string? error)
	{
		if (error is { Length: > 0 })
		{
			throw new InvalidOperationException($"Ollama reported: {error}");
		}
	}

	private static List<AiToolCall> Calls(ChatMessage? message)
		=> message?.ToolCalls?.Select(Translate).ToList() ?? [];

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
