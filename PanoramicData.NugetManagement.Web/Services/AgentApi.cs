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
	private const int Capacity = 1000;

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
		["clone"] = WorkKind.Clone,
		["git-sync"] = WorkKind.GitSync,
		["fix-all"] = WorkKind.FixAll,
		["fix-rule"] = WorkKind.FixRule,
		["commit-and-push"] = WorkKind.CommitAndPush,
		["publish"] = WorkKind.Publish
	};

	/// <summary>
	/// Kinds that leave the machine, and so are refused unless the repository is in the state that makes
	/// them reasonable.
	/// </summary>
	/// <remarks>
	/// Both guards sit here, ahead of the executors' own, so that an agent's loop cannot reach them with
	/// a repository the person would never have chosen. Publishing to nuget.org cannot be undone — a
	/// version, once published, can only be unlisted — so it additionally needs <c>confirm: true</c>.
	/// </remarks>
	private static string? Refusal(WorkKind kind, RepositoryDashboardRow row, bool confirm)
	{
		switch (kind)
		{
			case WorkKind.CommitAndPush:
				return row.IsClonedLocally ? null : "the repository is not cloned locally";

			case WorkKind.Publish:
				if (!confirm)
				{
					return "publishing is irreversible and needs confirm: true";
				}

				if (row.Assessment is null || row.TotalFailures != 0)
				{
					return $"the repository is not green ({row.TotalFailures} failing)";
				}

				if (row.IsWorkingTreeClean != true || row.HasUnpushedCommits == true || row.IsSyncedWithOrigin != true)
				{
					return "the working tree is not clean and in step with origin";
				}

				return null;

			default:
				return null;
		}
	}

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
					failing = FailingRuleIds(r),
					totalFailures = r.TotalFailures,
					staleIssues = r.StaleIssues.Count(),
					green = r.Assessment is not null && r.TotalFailures == 0,
					clean = r.IsWorkingTreeClean,
					unpushed = r.HasUnpushedCommits,
					packages = r.Packages.Select(pk => new { id = pk.PackageId, latest = pk.LatestVersion }),
					latestTag = r.LatestTag
				});

			return Results.Json(rows);
		});

		group.MapGet("/repos/{owner}/{name}", (
			DashboardCacheService cache,
			Remediations.RemediationRegistry remediations,
			string owner,
			string name) =>
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
					syncedWithOrigin = row.IsSyncedWithOrigin,
					status = row.Status.ToString(),
					statusMessage = row.StatusMessage,
					lastBuild = row.LastBuildState?.ToString(),
					lastTest = row.LastTestState?.ToString(),
					totalFailures = row.TotalFailures,
					staleIssues = row.StaleIssues.Select(i => new { i.Number, i.Title }),
					packages = row.Packages.Select(pk => new { id = pk.PackageId, latest = pk.LatestVersion }),
					latestTag = row.LatestTag,
					nextVersion = row.NextVersion,
					failingRules = row.Assessment?.RuleResults
						.Where(r => !r.Passed && r.IsApplicable)
						.Select(r => new
						{
							rule = r.RuleId,
							name = r.RuleName,
							severity = r.Severity.ToString(),
							message = r.Message,
							fix = FixRoute(r.RuleId, remediations),
							targets = r.Advisory?.Targets?.Select(t => t.Path),

							// What the rule itself says about the failure, which is more than its one-line
							// message — for the Codacy rules it is the individual issues. Cut, because an
							// advisory can run to many pages.
							detail = Truncate(r.Advisory?.Detail, 3000),
							data = r.Advisory?.Data
						})
				}));

		group.MapGet("/rules", (Remediations.RemediationRegistry remediations, AiPlaybookRegistry playbooks) =>
			Results.Json(PanoramicData.NugetManagement.Services.RuleRegistry.Rules.Select(rule => new
			{
				rule = rule.RuleId,
				name = rule.RuleName,
				category = rule.Category.ToString(),
				severity = rule.Severity.ToString(),
				fix = FixRoute(rule.RuleId, remediations),
				playbook = playbooks.For(rule.RuleId) is not null
			})));

		group.MapGet("/work", (AgentWorkObserver observer, string? kind, string? state, string? repo, int? take) =>
			Results.Json(observer.Items
				.Where(i => string.IsNullOrWhiteSpace(kind)
					|| string.Equals(i.Descriptor.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase))
				.Where(i => string.IsNullOrWhiteSpace(state)
					|| string.Equals(i.State.ToString(), state, StringComparison.OrdinalIgnoreCase))
				.Where(i => string.IsNullOrWhiteSpace(repo)
					|| string.Equals(i.RepositoryFullName, repo, StringComparison.OrdinalIgnoreCase))
				.Reverse()
				.Take(Math.Clamp(take ?? 100, 1, 1000))
				.Select(Describe)));

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
			WorkFanOut fanOut,
			Remediations.RemediationRegistry remediationRegistry) =>
		{
			// Everything queued here is the agent API's doing, and the ledger says so.
			using var origin = Governance.QueueOrigin.Use("agent-api");

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

				// "*" is every rule the app itself would offer Fix with AI for, one session per file where a
				// rule names files, minus anything the caller asks to skip.
				if (request.RuleId == "*")
				{
					var skip = new HashSet<string>(request.Skip ?? [], StringComparer.OrdinalIgnoreCase);

					var targets = AiFixCandidates.For(row, remediationRegistry)
						.Where(target => !skip.Contains(target.RuleId))
						.ToList();

					return Results.Json(new
					{
						queued = fanOut.EnqueueAiFix(row.Organization, row.RepositoryFullName, targets, consoleNodeKey: null),
						targets = targets.Select(t => t.ToString())
					});
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

			if (string.Equals(request.Kind, "unstick", StringComparison.OrdinalIgnoreCase))
			{
				if (!AiFixTargets.IsStuck(row))
				{
					return Results.Json(
						new { error = $"{row.RepositoryFullName} is not stuck: it needs uncommitted changes and to be out of sync" },
						statusCode: StatusCodes.Status409Conflict);
				}

				return Results.Json(new { queued = fanOut.EnqueueUnstick(row.Organization, row.RepositoryFullName, consoleNodeKey: null) ? 1 : 0 });
			}

			if (request.Kind is null || !_queueable.TryGetValue(request.Kind, out var kind))
			{
				return Results.Json(
					new { error = $"kind must be one of: ai-fix, unstick, {string.Join(", ", _queueable.Keys)}" },
					statusCode: StatusCodes.Status400BadRequest);
			}

			if (Refusal(kind, row, request.Confirm == true) is { } refusal)
			{
				return Results.Json(new { error = $"refused: {refusal}" }, statusCode: StatusCodes.Status409Conflict);
			}

			if (kind == WorkKind.FixRule && string.IsNullOrWhiteSpace(request.RuleId))
			{
				return Results.Json(new { error = "fix-rule needs ruleId" }, statusCode: StatusCodes.Status400BadRequest);
			}

			var parameters = kind == WorkKind.FixRule
				? new[] { ("ruleId", request.RuleId!.ToUpperInvariant()) }
				: [];

			var item = lanes.Enqueue(
				$"{kind} {row.RepositoryFullName}",
				WorkDescriptor.ForRepository(kind, row.Organization, row.RepositoryFullName, parameters),
				$"{kind}:{row.RepositoryFullName}:{string.Join(',', parameters.Select(p => p.Item2))}",
				step: null,
				consoleNodeKey: null);

			return Results.Json(new { queued = item is null ? 0 : 1, id = item?.Id });
		});

		MapGovernance(group);

		group.MapPost("/work/{id}/cancel", (WorkLaneService lanes, string id) =>
		{
			lanes.Cancel(id);

			return Results.Json(new { cancelled = id });
		});
	}

	/// <summary>
	/// The governance record: the ledger, the policy, and the packets the ledger points at.
	/// </summary>
	private static void MapGovernance(RouteGroupBuilder group)
	{
		group.MapGet("/ledger", (Governance.GovernanceLedger ledger, string? kind, string? repo, int? take, long? before) =>
			Results.Json(ledger.Query(kind, repo, take ?? 100, before)));

		group.MapGet("/ledger/verify", (Governance.GovernanceLedger ledger) => Results.Json(ledger.Verify()));

		group.MapPost("/ledger/note", (NoteRequest request, Governance.GovernanceLedger ledger) =>
		{
			if (string.IsNullOrWhiteSpace(request.Summary))
			{
				return Results.Json(new { error = "a note needs a summary" }, statusCode: StatusCodes.Status400BadRequest);
			}

			// Always attributed to the session, never to Autopilot: a note is a person or reviewer saying
			// something about the record, and must not be mistaken for the system's own action.
			var entry = ledger.Append(
				"claude-session",
				"note",
				request.Repository,
				request.RuleId,
				new { request.Summary, request.Data });

			return Results.Json(entry);
		});

		group.MapGet("/policy", (Governance.AutopilotPolicyStore store) => Results.Json(new { hash = store.PolicyHash, policy = store.Current }));

		group.MapPut("/policy", (PolicyUpdate request, Governance.AutopilotPolicyStore store) =>
		{
			if (request.Policy is null || string.IsNullOrWhiteSpace(request.Reason))
			{
				return Results.Json(new { error = "a policy change needs a policy and a reason" }, statusCode: StatusCodes.Status400BadRequest);
			}

			return Results.Json(new { hash = store.Update(request.Policy, "agent-api", request.Reason) });
		});

		group.MapGet("/packet", (Governance.ReviewPacketStore packets, string path, string sha256) =>
		{
			try
			{
				return packets.Read(new Governance.PacketReference(path, sha256, 0)) is { } packet
					? Results.Json(packet)
					: Results.Json(new { error = "no such packet" }, statusCode: StatusCodes.Status404NotFound);
			}
			catch (InvalidDataException ex)
			{
				return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
			}
		});
	}

	/// <summary>The request body for adding a note to the ledger.</summary>
	/// <param name="Summary">What is being recorded.</param>
	/// <param name="Repository">The repository it concerns, if any.</param>
	/// <param name="RuleId">The rule it concerns, if any.</param>
	/// <param name="Data">Anything further.</param>
	public sealed record NoteRequest(string? Summary, string? Repository, string? RuleId, System.Text.Json.JsonElement? Data);

	/// <summary>The request body for changing the policy.</summary>
	/// <param name="Policy">The new policy, whole.</param>
	/// <param name="Reason">Why. Required.</param>
	public sealed record PolicyUpdate(Governance.AutopilotPolicy? Policy, string? Reason);

	/// <summary>The request body for queueing work.</summary>
	/// <param name="Kind">One of <c>ai-fix</c>, <c>reassess</c>, <c>build</c>, <c>test</c>, <c>clone</c>.</param>
	/// <param name="Repository">The repository, as "owner/name".</param>
	/// <param name="RuleId">For <c>ai-fix</c>, the failing rule.</param>
	/// <param name="Path">For <c>ai-fix</c>, optionally the one file to fix.</param>
	/// <param name="Confirm">Required, and true, for <c>publish</c>.</param>
	/// <param name="Skip">For <c>ai-fix</c> with <c>ruleId: "*"</c>, rule ids to leave out.</param>
	public sealed record QueueRequest(
		string? Kind,
		string? Repository,
		string? RuleId,
		string? Path,
		bool? Confirm = null,
		string[]? Skip = null);

	/// <summary>
	/// How a rule can be fixed: by a deterministic remediation, remotely (graded on the published branch),
	/// outside the working tree, or by the model.
	/// </summary>
	private static string FixRoute(string ruleId, Remediations.RemediationRegistry remediations)
	{
		var rule = PanoramicData.NugetManagement.Services.RuleRegistry.Rules
			.FirstOrDefault(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

		if (remediations.Get(ruleId) is not null)
		{
			return rule is PanoramicData.NugetManagement.Rules.IRemotelyGraded ? "remediation+remote" : "remediation";
		}

		return rule switch
		{
			PanoramicData.NugetManagement.Rules.IFixedOutsideTheWorkingTree => "outside-tree",
			PanoramicData.NugetManagement.Rules.IRemotelyGraded => "ai+remote",
			_ => "ai"
		};
	}

	private static string? Truncate(string? text, int length)
		=> text is null || text.Length <= length ? text : text[..length] + "…";

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
