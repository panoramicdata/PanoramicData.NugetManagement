using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ollama.Api;
using Ollama.Api.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// How to reach Ollama directly, for the one thing the client library cannot do.
/// </summary>
/// <param name="BaseUri">The server's base address.</param>
/// <param name="ApiKey">An optional bearer key.</param>
/// <param name="IdleTimeout">How long the server may be silent before the turn is abandoned.</param>
/// <param name="Think">Whether the model may reason before answering.</param>
public sealed record OllamaDirectOptions(Uri BaseUri, string? ApiKey, TimeSpan IdleTimeout, bool Think);

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
public sealed class OllamaChatModel(
	OllamaClient client,
	string model,
	int? contextWindow,
	OllamaDirectOptions? direct = null) : IChatModel
{
	private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

	private static readonly JsonSerializerOptions _json = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

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

		messages.AddRange(conversation.Select(Translate));

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

		// The direct route exists for one field the client cannot send: <c>think</c>, which Ollama reads
		// from the top level of the request and silently ignores inside options. A reasoning model left to
		// think spent four minutes and five thousand tokens deliberating one turn of a one-line fix.
		if (direct is not null)
		{
			return await StreamAsync(
				DirectChunksAsync(request, direct, cancellationToken),
				onDelta ?? (_ => { }),
				cancellationToken).ConfigureAwait(false);
		}

		return onDelta is null
			? await CompleteAsync(request, cancellationToken).ConfigureAwait(false)
			: await StreamAsync(client.Chat.ChatStreamAsync(request, cancellationToken), onDelta, cancellationToken)
				.ConfigureAwait(false);
	}

	/// <summary>
	/// The request as the JSON Ollama receives it, with <c>think</c> added at the top level.
	/// </summary>
	/// <param name="request">The request to send.</param>
	/// <param name="think">Whether the model may reason before answering.</param>
	internal static string BuildDirectBody(ChatRequest request, bool think)
	{
		var body = JsonSerializer.SerializeToNode(request, _json)!.AsObject();
		body["stream"] = true;
		body["think"] = think;

		return body.ToJsonString();
	}

	/// <summary>
	/// Streams a chat turn over plain HTTP, one decoded chunk per line the server sends.
	/// </summary>
	/// <remarks>
	/// The timeout is idle rather than total: it is reset on every line, so a long turn that keeps
	/// producing tokens is never cut off and a stalled one is.
	/// </remarks>
	private static async IAsyncEnumerable<ChatResponse> DirectChunksAsync(
		ChatRequest request,
		OllamaDirectOptions options,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		idle.CancelAfter(options.IdleTimeout);

		using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, "api/chat"))
		{
			Content = new StringContent(BuildDirectBody(request, options.Think), Encoding.UTF8, "application/json")
		};

		if (!string.IsNullOrWhiteSpace(options.ApiKey))
		{
			message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
		}

		using var response = await _http
			.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, idle.Token)
			.ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			var detail = await response.Content.ReadAsStringAsync(idle.Token).ConfigureAwait(false);

			throw IsMalformedOutput(detail)
				? new AiModelOutputException($"Ollama returned {(int)response.StatusCode}: {detail}")
				: new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {detail}");
		}

		var stream = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
		await using var disposeStream = stream.ConfigureAwait(false);
		using var reader = new StreamReader(stream);

		while (await reader.ReadLineAsync(idle.Token).ConfigureAwait(false) is { } line)
		{
			idle.CancelAfter(options.IdleTimeout);

			if (line.Length == 0)
			{
				continue;
			}

			yield return JsonSerializer.Deserialize<ChatResponse>(line, _json)
				?? throw new InvalidOperationException("Ollama sent a line that was not a chat response.");
		}
	}

	/// <summary>
	/// One turn, waited for in full.
	/// </summary>
	private async Task<AiModelTurn> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
	{
		var response = await client.Chat.ChatAsync(request, cancellationToken).ConfigureAwait(false);

		Throw(response.Error);

		return new AiModelTurn(
			response.Message?.Content,
			Calls(response.Message),
			response.Message?.Thinking is { Length: > 0 } thinking ? thinking : null,
			Measure(response));
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
	private static async Task<AiModelTurn> StreamAsync(
		IAsyncEnumerable<ChatResponse> chunks,
		Action<AiStreamDelta> onDelta,
		CancellationToken cancellationToken)
	{
		var content = new StringBuilder();
		var thinkingText = new StringBuilder();
		var calls = new List<AiToolCall>();
		AiTurnMetrics? metrics = null;

		await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
		{
			Throw(chunk.Error);

			// Only the closing chunk carries the server's measurements; earlier ones leave them null.
			metrics = Measure(chunk) ?? metrics;

			if (chunk.Message is not { } message)
			{
				continue;
			}

			if (message.Thinking is { Length: > 0 } thinking)
			{
				thinkingText.Append(thinking);
				onDelta(new AiStreamDelta(AiDeltaKind.Thinking, thinking));
			}

			if (message.Content is { Length: > 0 } fragment)
			{
				content.Append(fragment);
				onDelta(new AiStreamDelta(AiDeltaKind.Content, fragment));
			}

			calls.AddRange(Calls(message));
		}

		return new AiModelTurn(
			content.Length == 0 ? null : content.ToString(),
			calls,
			thinkingText.Length == 0 ? null : thinkingText.ToString(),
			metrics);
	}

	/// <summary>
	/// The server's own account of a turn, or null when it gave none.
	/// </summary>
	/// <remarks>
	/// Ollama reports durations in nanoseconds. A response with no total is one that was not the closing
	/// chunk, or came from a server that does not measure — either way there is nothing to report.
	/// </remarks>
	private static AiTurnMetrics? Measure(ChatResponse response)
	{
		if (response.TotalDuration is not { } total)
		{
			return null;
		}

		static TimeSpan Nanoseconds(long? value) => TimeSpan.FromTicks((value ?? 0) / 100);

		return new AiTurnMetrics(
			response.PromptEvalCount ?? 0,
			Nanoseconds(response.PromptEvalDuration),
			response.EvalCount ?? 0,
			Nanoseconds(response.EvalDuration),
			Nanoseconds(response.LoadDuration),
			Nanoseconds(total));
	}

	/// <summary>
	/// One conversation message in Ollama's form, with an assistant's tool calls carried across.
	/// </summary>
	private static ChatMessage Translate(AiMessage message) => new()
	{
		Role = message.Role,
		Content = message.Content,
		ToolCalls = message.ToolCalls is { Count: > 0 } calls
			? [.. calls.Select(call => new ChatToolCall
			{
				Function = new ChatToolFunctionCall
				{
					Name = call.Name,
					Arguments = call.Arguments.ToDictionary(a => a.Key, a => (object?)a.Value, StringComparer.Ordinal)
				}
			})]
			: null
	};

	/// <summary>Fails the turn on an error the server reported, which is not an HTTP failure.</summary>
	private static void Throw(string? error)
	{
		if (error is not { Length: > 0 })
		{
			return;
		}

		throw IsMalformedOutput(error)
			? new AiModelOutputException($"Ollama reported: {error}")
			: new InvalidOperationException($"Ollama reported: {error}");
	}

	/// <summary>
	/// Whether a server error is the model's own badly formed output rather than a fault in the setup.
	/// </summary>
	/// <param name="error">The server's error text.</param>
	internal static bool IsMalformedOutput(string error)
		=> error.Contains("syntax error", StringComparison.OrdinalIgnoreCase)
			|| error.Contains("error parsing tool call", StringComparison.OrdinalIgnoreCase)
			|| error.Contains("unexpected end", StringComparison.OrdinalIgnoreCase)
			|| error.Contains("invalid character", StringComparison.OrdinalIgnoreCase);

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
