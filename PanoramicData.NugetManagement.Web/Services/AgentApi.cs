using System.Net;
using System.Security.Cryptography;
using System.Text;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Remembers every work item the lanes have shown, including after they finish.
/// </summary>
/// <remarks>
/// <see cref="WorkLaneService"/> drops an item the moment it completes, which is the right thing for the
/// tree and the wrong thing for anyone who wants to know how it went. Holding the item itself, not a
/// copy, keeps its live transcript readable afterwards. Bounded, because a long estate sweep would
/// otherwise keep every transcript it ever produced.
/// </remarks>
public sealed class AgentWorkObserver
{
	private const int Capacity = 200;

	private readonly Lock _gate = new();
	private readonly List<WorkItem> _seen = [];
	private readonly HashSet<string> _ids = [];

	/// <summary>Starts watching the lanes.</summary>
	/// <param name="lanes">The lanes to watch.</param>
	public AgentWorkObserver(WorkLaneService lanes)
	{
		lanes.Changed += () => Capture(lanes);
		Capture(lanes);
	}

	/// <summary>Every item seen, oldest first.</summary>
	public IReadOnlyList<WorkItem> Items
	{
		get
		{
			lock (_gate)
			{
				return [.. _seen];
			}
		}
	}

	/// <summary>One item by id, or null.</summary>
	/// <param name="id">The work item id.</param>
	public WorkItem? Find(string id)
	{
		lock (_gate)
		{
			return _seen.FirstOrDefault(item => item.Id == id);
		}
	}

	private void Capture(WorkLaneService lanes)
	{
		lock (_gate)
		{
			foreach (var item in lanes.Lanes.SelectMany(lane => lane.Items))
			{
				if (_ids.Add(item.Id))
				{
					_seen.Add(item);
				}
			}

			while (_seen.Count > Capacity)
			{
				_ids.Remove(_seen[0].Id);
				_seen.RemoveAt(0);
			}
		}
	}
}

/// <summary>
/// The <c>/agent</c> endpoints: a small, loopback-only, token-protected window onto the tree for a coding
/// agent working alongside the person.
/// </summary>
/// <remarks>
/// Exists so an agent can see what the tree shows and start the same work the buttons start, without a
/// person relaying either. It is deliberately narrower than the UI. It reads state and transcripts, and
/// queues only work that changes nothing outside the local clone — AI fix, re-assess, build, test,
/// clone. Commit and push, publish and every other outward-facing action are not reachable from here,
/// and adding one should be a decision made in this file rather than a side effect of a new
/// <see cref="WorkKind"/>.
/// <para>
/// Three independent gates, all of which must hold: the API is only mapped when a token is configured,
/// the caller must be on loopback, and the token must match in constant time.
/// </para>
/// </remarks>
public static class AgentApi
{
	/// <summary>The kinds of work the API will queue, by the name a caller uses.</summary>
	private static readonly Dictionary<string, WorkKind> _queueable = new(StringComparer.OrdinalIgnoreCase)
	{
		["reassess"] = WorkKind.Reassess,
		["build"] = WorkKind.Build,
		["test"] = WorkKind.Test,
		["clone"] = WorkKind.Clone
	};

	/// <summary>Maps the endpoints, or maps nothing when no token is configured.</summary>
	/// <param name="app">The application.</param>
	/// <param name="token">The configured bearer token, or null/empty to leave the API unmapped.</param>
	public static void MapAgentApi(this WebApplication app, string? token)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return;
		}

		var expected = Encoding.UTF8.GetBytes(token);

		var group = app.MapGroup("/agent")
			.DisableAntiforgery()
			.AddEndpointFilter(async (context, next) =>
			{
				var http = context.HttpContext;

				if (http.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
				{
					return Results.Json(new { error = "loopback only" }, statusCode: StatusCodes.Status403Forbidden);
				}

				var header = http.Request.Headers.Authorization.ToString();
				var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
					? Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim())
					: [];

				return presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected)
					? await next(context).ConfigureAwait(false)
					: Results.Json(new { error = "bad token" }, statusCode: StatusCodes.Status401Unauthorized);
			});

		group.MapGet("/state", (
			DashboardCacheService cache,
			WorkLaneService lanes,
			RuntimeSettingsService settings,
			GitHubTokenProvider gitHub) =>
		{
			var rows = cache.GetCachedRows() ?? [];
			var items = lanes.Lanes.SelectMany(lane => lane.Items).ToList();

			return Results.Json(new
			{
				repositories = rows.Count,
				cloned = rows.Count(r => r.IsClonedLocally),
				lastRefreshUtc = cache.LastRefreshUtc,
				work = new
				{
					pending = items.Count(i => i.State == WorkItemState.Pending),
					running = items.Count(i => i.State == WorkItemState.Running)
				},
				ollama = new { configured = settings.Ollama.IsConfigured, model = settings.Ollama.Model },
				gitHubTokenAvailable = !string.IsNullOrEmpty(gitHub.AccessToken)
			});
		});

		group.MapGet("/repos", (DashboardCacheService cache, string? q, bool? cloned, string? failing) =>
		{
			var rows = (cache.GetCachedRows() ?? [])
				.Where(r => string.IsNullOrWhiteSpace(q)
					|| r.RepositoryFullName.Contains(q, StringComparison.OrdinalIgnoreCase))
				.Where(r => cloned is null || r.IsClonedLocally == cloned)
				.Where(r => string.IsNullOrWhiteSpace(failing)
					|| r.Assessment?.RuleResults.Any(x => !x.Passed
						&& string.Equals(x.RuleId, failing, StringComparison.OrdinalIgnoreCase)) == true)
				.Take(200)
				.Select(r => new
				{
					repository = r.RepositoryFullName,
					cloned = r.IsClonedLocally,
					status = r.Status.ToString(),
					failing = FailingRuleIds(r)
				});

			return Results.Json(rows);
		});

		group.MapGet("/repos/{owner}/{name}", (DashboardCacheService cache, string owner, string name) =>
			cache.GetRow($"{owner}/{name}") is not { } row
				? Results.Json(new { error = "no such repository" }, statusCode: StatusCodes.Status404NotFound)
				: Results.Json(new
				{
					repository = row.RepositoryFullName,
					cloned = row.IsClonedLocally,
					localPath = row.LocalPath,
					branch = row.CurrentBranch,
					workingTreeClean = row.IsWorkingTreeClean,
					unpushedCommits = row.HasUnpushedCommits,
					status = row.Status.ToString(),
					statusMessage = row.StatusMessage,
					lastBuild = row.LastBuildState?.ToString(),
					lastTest = row.LastTestState?.ToString(),
					failingRules = row.Assessment?.RuleResults
						.Where(r => !r.Passed && r.IsApplicable)
						.Select(r => new
						{
							rule = r.RuleId,
							name = r.RuleName,
							severity = r.Severity.ToString(),
							message = r.Message
						})
				}));

		group.MapGet("/work", (AgentWorkObserver observer) => Results.Json(
			observer.Items.Select(Describe).Reverse().Take(100)));

		group.MapGet("/work/{id}", (AgentWorkObserver observer, string id) =>
			observer.Find(id) is { } item
				? Results.Json(Describe(item))
				: Results.Json(new { error = "no such work item" }, statusCode: StatusCodes.Status404NotFound));

		group.MapGet("/work/{id}/transcript", (AgentWorkObserver observer, string id, int? since) =>
		{
			if (observer.Find(id) is not { } item)
			{
				return Results.Json(new { error = "no such work item" }, statusCode: StatusCodes.Status404NotFound);
			}

			var lines = item.Transcript.Snapshot();
			var from = Math.Clamp(since ?? 0, 0, lines.Count);

			return Results.Json(new
			{
				total = lines.Count,
				state = item.State.ToString(),
				lines = lines.Skip(from).Select((line, i) => new
				{
					index = from + i,
					kind = line.Kind.ToString(),
					at = line.AtUtc,
					text = line.Text
				})
			});
		});

		group.MapPost("/work", (
			QueueRequest request,
			DashboardCacheService cache,
			WorkLaneService lanes,
			WorkFanOut fanOut) =>
		{
			if (string.IsNullOrWhiteSpace(request.Repository) || cache.GetRow(request.Repository) is not { } row)
			{
				return Results.Json(new { error = "no such repository" }, statusCode: StatusCodes.Status404NotFound);
			}

			if (string.Equals(request.Kind, "ai-fix", StringComparison.OrdinalIgnoreCase))
			{
				if (string.IsNullOrWhiteSpace(request.RuleId))
				{
					return Results.Json(new { error = "ai-fix needs ruleId" }, statusCode: StatusCodes.Status400BadRequest);
				}

				var failing = row.Assessment?.RuleResults.Any(r => !r.Passed
					&& string.Equals(r.RuleId, request.RuleId, StringComparison.OrdinalIgnoreCase)) == true;

				if (!failing)
				{
					return Results.Json(new { error = $"{request.RuleId} is not failing on {row.RepositoryFullName}" }, statusCode: StatusCodes.Status409Conflict);
				}

				var queuedFixes = fanOut.EnqueueAiFix(
					row.Organization,
					row.RepositoryFullName,
					[new AiFixTarget(request.RuleId.ToUpperInvariant(), string.IsNullOrWhiteSpace(request.Path) ? null : request.Path)],
					consoleNodeKey: null);

				return Results.Json(new { queued = queuedFixes });
			}

			if (request.Kind is null || !_queueable.TryGetValue(request.Kind, out var kind))
			{
				return Results.Json(
					new { error = $"kind must be one of: ai-fix, {string.Join(", ", _queueable.Keys)}" },
					statusCode: StatusCodes.Status400BadRequest);
			}

			var item = lanes.Enqueue(
				$"{kind} {row.RepositoryFullName}",
				WorkDescriptor.ForRepository(kind, row.Organization, row.RepositoryFullName),
				$"{kind}:{row.RepositoryFullName}:",
				step: null,
				consoleNodeKey: null);

			return Results.Json(new { queued = item is null ? 0 : 1, id = item?.Id });
		});

		group.MapPost("/work/{id}/cancel", (WorkLaneService lanes, string id) =>
		{
			lanes.Cancel(id);

			return Results.Json(new { cancelled = id });
		});
	}

	/// <summary>The request body for queueing work.</summary>
	/// <param name="Kind">One of <c>ai-fix</c>, <c>reassess</c>, <c>build</c>, <c>test</c>, <c>clone</c>.</param>
	/// <param name="Repository">The repository, as "owner/name".</param>
	/// <param name="RuleId">For <c>ai-fix</c>, the failing rule.</param>
	/// <param name="Path">For <c>ai-fix</c>, optionally the one file to fix.</param>
	public sealed record QueueRequest(string? Kind, string? Repository, string? RuleId, string? Path);

	private static List<string> FailingRuleIds(RepositoryDashboardRow row)
		=> row.Assessment?.RuleResults
			.Where(r => !r.Passed && r.IsApplicable)
			.Select(r => r.RuleId)
			.Order(StringComparer.Ordinal)
			.ToList() ?? [];

	private static object Describe(WorkItem item) => new
	{
		id = item.Id,
		title = item.Title,
		kind = item.Descriptor.Kind.ToString(),
		repository = item.RepositoryFullName,
		state = item.State.ToString(),
		succeeded = item.Succeeded,
		progress = item.Progress,
		transcriptLines = item.Transcript.Count
	};
}
