using System.Text.Json;
using System.Text.Json.Nodes;

namespace PanoramicData.NugetManagement.Web.Services.Governance;

/// <summary>
/// Who asked for a piece of work, carried from the moment it is queued to the moment it is recorded.
/// </summary>
/// <remarks>
/// Ambient rather than a parameter on every queueing method, because work is queued from a dozen places —
/// the buttons, the agent API, and soon Autopilot — and the ledger must say which, without each of them
/// having to remember to. Whoever queues work wraps the queueing in <see cref="Use"/>.
/// </remarks>
public static class QueueOrigin
{
	private static readonly AsyncLocal<string?> _current = new();

	/// <summary>The origin in force where work is being queued, or null for a person at the keyboard.</summary>
	public static string? Current => _current.Value;

	/// <summary>
	/// Marks the work queued inside the returned scope as coming from <paramref name="origin"/>.
	/// </summary>
	/// <param name="origin">For example <c>agent-api</c> or <c>autopilot</c>.</param>
	public static IDisposable Use(string origin)
	{
		var previous = _current.Value;
		_current.Value = origin;

		return new Scope(previous);
	}

	private sealed class Scope(string? previous) : IDisposable
	{
		public void Dispose() => _current.Value = previous;
	}
}

/// <summary>
/// Turns what the executors did into ledger entries and review packets.
/// </summary>
/// <remarks>
/// One place, so the executors gain a line each and not a page. Recording never throws into the work it is
/// recording: a ledger that cannot be written is logged and the action stands, because failing a fix that
/// already happened would only make the record say less than the truth.
/// </remarks>
public sealed class GovernanceRecorder(
	GovernanceLedger ledger,
	ReviewPacketStore packets,
	ILogger<GovernanceRecorder> logger)
{
	private const int MaxToolResultCharacters = 4_000;
	private const int MaxModelTextCharacters = 2_000;
	private const int MaxDiffCharacters = 100_000;

	/// <summary>Who is acting: whoever queued the work, or "user" for a person using the buttons.</summary>
	/// <param name="item">The work item.</param>
	public static string ActorFor(Models.WorkItem item) => item.QueuedBy ?? "user";

	/// <summary>
	/// Records one AI session, with a packet holding what a reviewer would need.
	/// </summary>
	/// <param name="item">The work item the session ran for.</param>
	/// <param name="request">What the model was asked.</param>
	/// <param name="model">The model, as configured.</param>
	/// <param name="outcome">What the session reported, or null if it ended without one.</param>
	/// <param name="disposition">What became of its changes: <c>applied</c>, <c>kept</c>, <c>reverted</c> or <c>cancelled</c>.</param>
	/// <param name="files">The files the session wrote.</param>
	/// <param name="diff">What it changed, captured before anything was put back.</param>
	/// <param name="sessionLogPath">The session's own log, from which the packet is built.</param>
	public void RecordAiSession(
		Models.WorkItem item,
		AiFixRequest request,
		string? model,
		AiFixOutcome? outcome,
		string disposition,
		IReadOnlyCollection<string> files,
		string diff,
		string? sessionLogPath)
	{
		Safely(() =>
		{
			var events = ReadSessionEvents(sessionLogPath);

			PacketReference? packet = null;
			string? packetProblem = null;

			try
			{
				packet = packets.Write(new
				{
					kind = "ai_session",
					repository = request.RepositoryFullName,
					ruleId = request.RuleId,
					model,
					promptVersion = AiFixPrompt.Version,
					systemPrompt = request.SystemPrompt,
					task = request.Task,
					outcome = outcome is null ? null : new { outcome.Succeeded, outcome.Attempts, outcome.Summary },
					disposition,
					files,
					diff = Truncate(diff, MaxDiffCharacters),
					events
				});
			}
			catch (PacketRefusedException ex)
			{
				// The ledger entry is still written, saying why there is no packet. A missing packet that
				// explains itself is a finding; a missing entry is a hole in the record.
				packetProblem = ex.Message;
			}

			var turns = events.Count(e => e is JsonObject o && (string?)o["type"] == "turn");

			ledger.Append(
				ActorFor(item),
				"ai_session",
				request.RepositoryFullName,
				request.RuleId,
				new
				{
					workItemId = item.Id,
					model,
					promptVersion = AiFixPrompt.Version,
					turns,
					succeeded = outcome?.Succeeded,
					attempts = outcome?.Attempts,
					summary = outcome?.Summary,
					disposition,
					files,
					packet,
					packetProblem
				});
		});
	}

	/// <summary>
	/// Records the application of deterministic remediations.
	/// </summary>
	/// <param name="item">The work item.</param>
	/// <param name="repositoryFullName">The repository.</param>
	/// <param name="ruleId">The rule, or null for a whole pass.</param>
	/// <param name="applied">What was applied, as the remediation reported it.</param>
	public void RecordRemediation(Models.WorkItem item, string repositoryFullName, string? ruleId, IReadOnlyCollection<string> applied)
		=> Safely(() => ledger.Append(
			ActorFor(item),
			"remediation_applied",
			repositoryFullName,
			ruleId,
			new { workItemId = item.Id, count = applied.Count, applied }));

	/// <summary>
	/// Records a commit and push, successful or refused.
	/// </summary>
	/// <param name="item">The work item.</param>
	/// <param name="repositoryFullName">The repository.</param>
	/// <param name="succeeded">Whether it was pushed.</param>
	/// <param name="refusalReason">Why a guard refused it, if one did.</param>
	/// <param name="cloneRoot">The clone, from which the commit is described.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task RecordPushAsync(
		Models.WorkItem item,
		string repositoryFullName,
		bool succeeded,
		string? refusalReason,
		string? cloneRoot,
		CancellationToken cancellationToken)
	{
		try
		{
			string? sha = null;
			string? branch = null;
			var files = new List<string>();
			PacketReference? packet = null;
			string? packetProblem = null;

			if (succeeded && cloneRoot is not null)
			{
				sha = await GitAsync(cloneRoot, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
				branch = await GitAsync(cloneRoot, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);

				var names = await GitAsync(cloneRoot, ["show", "--name-only", "--format=", "HEAD"], cancellationToken).ConfigureAwait(false);
				files.AddRange((names ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

				var patch = await GitAsync(cloneRoot, ["show", "--no-color", "--format=%H%n%s", "HEAD"], cancellationToken).ConfigureAwait(false);

				try
				{
					packet = packets.Write(new { kind = "push", repository = repositoryFullName, sha, branch, files, diff = Truncate(patch ?? string.Empty, MaxDiffCharacters) });
				}
				catch (PacketRefusedException ex)
				{
					packetProblem = ex.Message;
				}
			}

			ledger.Append(
				ActorFor(item),
				"push",
				repositoryFullName,
				data: new { workItemId = item.Id, succeeded, refusalReason, sha, branch, files, packet, packetProblem });
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			logger.LogWarning(ex, "Could not record a push to {Repository} in the ledger", repositoryFullName);
		}
	}

	/// <summary>
	/// Records an attempt to publish packages.
	/// </summary>
	/// <param name="item">The work item.</param>
	/// <param name="repositoryFullName">The repository.</param>
	/// <param name="succeeded">Whether the publish reported success.</param>
	/// <param name="packageIds">The packages the repository publishes.</param>
	/// <param name="latestTagBefore">The newest tag before the attempt.</param>
	/// <param name="nextVersion">The version the attempt was for.</param>
	/// <param name="failuresAtTheTime">How many rules were failing when it was attempted.</param>
	public void RecordPublish(
		Models.WorkItem item,
		string repositoryFullName,
		bool succeeded,
		IReadOnlyCollection<string> packageIds,
		string? latestTagBefore,
		string? nextVersion,
		int failuresAtTheTime)
		=> Safely(() => ledger.Append(
			ActorFor(item),
			"publish",
			repositoryFullName,
			data: new { workItemId = item.Id, succeeded, packageIds, latestTagBefore, nextVersion, failuresAtTheTime }));

	/// <summary>
	/// What a session changed: the diff of tracked files, and the content of new ones.
	/// </summary>
	/// <param name="cloneRoot">The repository clone.</param>
	/// <param name="files">The files the session wrote, relative to the clone.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	public async Task<string> CaptureDiffAsync(string cloneRoot, IReadOnlyCollection<string> files, CancellationToken cancellationToken)
	{
		if (files.Count == 0)
		{
			return string.Empty;
		}

		try
		{
			var arguments = new List<string> { "diff", "--no-color", "--" };
			arguments.AddRange(files);

			var tracked = await GitAsync(cloneRoot, arguments, cancellationToken).ConfigureAwait(false) ?? string.Empty;

			var untracked = await GitAsync(cloneRoot, ["ls-files", "--others", "--exclude-standard", "--", .. files], cancellationToken).ConfigureAwait(false) ?? string.Empty;
			var builder = new System.Text.StringBuilder(tracked);

			foreach (var path in untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var full = Path.Combine(cloneRoot, path);

				if (File.Exists(full))
				{
					builder.AppendLine().AppendLine($"=== new file: {path}").Append(Truncate(await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false), 20_000));
				}
			}

			return builder.ToString();
		}
		catch (Exception ex) when (ex is IOException or InvalidOperationException)
		{
			logger.LogWarning(ex, "Could not capture the diff for a packet in {Clone}", cloneRoot);

			return string.Empty;
		}
	}

	/// <summary>
	/// A session log's events, with the bulky fields cut so a packet stays readable.
	/// </summary>
	/// <param name="path">The session log, or null.</param>
	internal static List<JsonNode?> ReadSessionEvents(string? path)
	{
		var events = new List<JsonNode?>();

		if (path is not { Length: > 0 } || !File.Exists(path))
		{
			return events;
		}

		foreach (var line in File.ReadLines(path).Where(line => line.Length > 0))
		{
			JsonNode? node;

			try
			{
				node = JsonNode.Parse(line);
			}
			catch (JsonException)
			{
				continue;
			}

			if (node is JsonObject entry)
			{
				Cut(entry, "content", MaxToolResultCharacters);
				Cut(entry, "thinking", MaxModelTextCharacters);
				Cut(entry, "text", MaxModelTextCharacters);
				Cut(entry, "message", MaxToolResultCharacters);
			}

			events.Add(node);
		}

		return events;
	}

	private static void Cut(JsonObject entry, string name, int limit)
	{
		if (entry[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > limit)
		{
			entry[name] = Truncate(text, limit);
		}
	}

	private static string Truncate(string text, int limit)
		=> text.Length <= limit ? text : text[..limit] + $"… [{text.Length - limit} more characters]";

	private static async Task<string?> GitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		var (exitCode, output) = await LocalRepoService.RunCommandAsync(workingDirectory, "git", arguments, cancellationToken).ConfigureAwait(false);

		return exitCode == 0 ? output.Trim() : null;
	}

	private void Safely(Action write)
	{
		try
		{
			write();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			logger.LogWarning(ex, "Could not write to the governance ledger");
		}
	}
}
