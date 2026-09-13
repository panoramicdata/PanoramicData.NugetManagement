using System.Text.Json;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// A parsed verdict, and what went wrong if anything did.
/// </summary>
/// <param name="Verdict">Always populated. An unreadable answer becomes an escalation.</param>
/// <param name="Note">
/// What the parser had to do, when it had to do anything — shown on the panel so an escalation caused
/// by malformed output is not mistaken for a judgement about the issue.
/// </param>
public sealed record IssueVerdictParseResult(IssueVerdict Verdict, string? Note);

/// <summary>
/// Turns what the model said into something typed, or into an escalation.
/// </summary>
/// <remarks>
/// This is where free-form output stops being text, which makes it a security boundary. Every path
/// that cannot be understood ends at <see cref="IssueAction.Escalate"/> rather than at an exception or
/// a guess: a human reading the issue is the safe direction, and a parser that guessed would be the
/// one place malformed output could still cause an action.
/// <para>
/// The public reply is built here rather than taken from the model, so a draft that will not fit
/// <see cref="IssueReplyTemplate"/> escalates instead of being trimmed into shape. Trimming would
/// hide the only signal that the model had started writing freely.
/// </para>
/// </remarks>
public static class IssueVerdictParser
{
	/// <summary>
	/// Parses one model response.
	/// </summary>
	/// <param name="response">Whatever the model returned, fenced or not.</param>
	public static IssueVerdictParseResult Parse(string response)
	{
		var json = ExtractJson(response);

		if (json is null)
		{
			return Escalated("The model did not return a verdict in the required form.");
		}

		JsonElement root;

		try
		{
			using var document = JsonDocument.Parse(json);
			root = document.RootElement.Clone();
		}
		catch (JsonException)
		{
			return Escalated("The model's verdict was not valid JSON.");
		}

		if (root.ValueKind is not JsonValueKind.Object)
		{
			return Escalated("The model's verdict was not an object.");
		}

		var reasoning = ReadString(root, "reasoning") ?? "The model gave no reasoning.";

		if (!TryReadEnum<IssueAction>(root, "action", out var action))
		{
			return Escalated($"The model asked for an action nobody here implements. It said: {reasoning}");
		}

		if (!TryReadRisks(root, out var risks))
		{
			return Escalated(
				$"The model flagged a risk nobody here recognises, so it is being treated as a "
					+ $"warning rather than dropped. It said: {reasoning}");
		}

		var confidence = TryReadEnum<IssueConfidence>(root, "confidence", out var parsed)
			? parsed
			: IssueConfidence.Low;

		return action switch
		{
			IssueAction.Fix => FromFix(root, confidence, risks, reasoning),
			IssueAction.Answer or IssueAction.Reject => FromReply(root, action, confidence, risks, reasoning),
			_ => new IssueVerdictParseResult(
				new IssueVerdict(IssueAction.Escalate, confidence, risks, reasoning, null, null), null)
		};
	}

	private static IssueVerdictParseResult FromFix(
		JsonElement root,
		IssueConfidence confidence,
		IReadOnlyList<IssueRiskFlag> risks,
		string reasoning)
	{
		if (!root.TryGetProperty("brief", out var brief) || brief.ValueKind is not JsonValueKind.Object)
		{
			return Escalated($"The model asked for a fix but described none. It said: {reasoning}");
		}

		var goal = ReadString(brief, "goal");
		var endState = ReadString(brief, "expectedEndState");
		var paths = ReadStringArray(brief, "paths");

		return goal is null || endState is null || paths.Count == 0
			? Escalated($"The model's fix brief was incomplete. It said: {reasoning}")
			: new IssueVerdictParseResult(
				new IssueVerdict(
					IssueAction.Fix,
					confidence,
					risks,
					reasoning,
					null,
					new IssueFixBrief(goal, paths, endState, ReadString(brief, "relatedRuleId"))),
				null);
	}

	private static IssueVerdictParseResult FromReply(
		JsonElement root,
		IssueAction action,
		IssueConfidence confidence,
		IReadOnlyList<IssueRiskFlag> risks,
		string reasoning)
	{
		var classification = ReadString(root, "replyClassification");
		var reason = ReadString(root, "replyReason");

		if (classification is null || reason is null)
		{
			return Escalated($"The model chose {action} but drafted no reply. It said: {reasoning}");
		}

		var reply = IssueReplyTemplate.Build(
			action, classification, reason, ReadString(root, "replyWhatWouldHelp"));

		return reply.Accepted
			? new IssueVerdictParseResult(
				new IssueVerdict(action, confidence, risks, reasoning, reply.Reply, null), null)
			: Escalated($"The drafted reply would not fit the template: {reply.Refusal}");
	}

	private static IssueVerdictParseResult Escalated(string note)
		=> new(
			new IssueVerdict(IssueAction.Escalate, IssueConfidence.Low, [], note, null, null),
			note);

	/// <summary>
	/// Finds the JSON object in a response, fence or no fence.
	/// </summary>
	/// <remarks>
	/// Models habitually wrap JSON in a markdown fence and put a sentence either side of it. Refusing
	/// that would escalate a large share of perfectly good verdicts for no security benefit, since what
	/// is parsed afterwards is the same text either way.
	/// </remarks>
	private static string? ExtractJson(string response)
	{
		if (string.IsNullOrWhiteSpace(response))
		{
			return null;
		}

		var start = response.IndexOf('{');
		var end = response.LastIndexOf('}');

		return start >= 0 && end > start
			? response[start..(end + 1)]
			: null;
	}

	private static string? ReadString(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(value.GetString())
				? value.GetString()
				: null;

	private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Array
			? [.. value.EnumerateArray()
				.Where(item => item.ValueKind is JsonValueKind.String)
				.Select(item => item.GetString()!)
				.Where(item => !string.IsNullOrWhiteSpace(item))]
			: [];

	private static bool TryReadEnum<T>(JsonElement root, string name, out T value) where T : struct, Enum
	{
		value = default;
		var text = ReadString(root, name);

		return text is not null && Enum.TryParse(text, ignoreCase: true, out value)
			&& Enum.IsDefined(value);
	}

	private static bool TryReadRisks(JsonElement root, out IReadOnlyList<IssueRiskFlag> risks)
	{
		var flags = new List<IssueRiskFlag>();

		foreach (var name in ReadStringArray(root, "risks"))
		{
			if (!Enum.TryParse<IssueRiskFlag>(name, ignoreCase: true, out var flag)
				|| !Enum.IsDefined(flag))
			{
				risks = [];
				return false;
			}

			flags.Add(flag);
		}

		risks = flags;
		return true;
	}
}
