using Microsoft.AspNetCore.Authentication;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Runs queued work. One method per <see cref="WorkKind"/>, none of which knows anything about a
/// browser.
/// </summary>
/// <remarks>
/// These bodies used to live in the dashboard component and were executed by the circuit that
/// queued them, which meant closing the tab cancelled the work and every body was free to reach
/// into the component's fields, clear its console and write to its clipboard. With up to twenty
/// lanes running at once none of that survives: there is no one console to clear, no one clipboard
/// to win, and no circuit to render. What each body needs it now takes from the cache and the
/// descriptor, and what it has to say it logs.
/// </remarks>
/// <param name="dashboard">Discovery, assessment, remediation, build, test and publish.</param>
/// <param name="cache">The estate as last discovered; the source of the row each item acts on.</param>
/// <param name="localRepo">Clones and the git operations performed directly on them.</param>
/// <param name="runtimeSettings">User settings, chiefly which repositories are excluded.</param>
/// <param name="fanOut">Turns a discovery's findings into one queued item per repository.</param>
/// <param name="httpContextAccessor">
/// The signed-in user's GitHub token, when there is a request to read it from.
/// </param>
/// <param name="logger">
/// Where the work narrates itself. The UI console mirrors this category, so a line logged here
/// reaches the console the item was queued from without the work knowing that console exists.
/// </param>
public sealed class WorkExecutors(
	DashboardService dashboard,
	DashboardCacheService cache,
	LocalRepoService localRepo,
	RuntimeSettingsService runtimeSettings,
	WorkFanOut fanOut,
	IHttpContextAccessor httpContextAccessor,
	ILogger<WorkExecutors> logger)
{
	/// <summary>Every kind this service knows how to run.</summary>
	/// <remarks>
	/// Exposed so a test can assert it covers <see cref="WorkKind"/> in full. A kind that can be
	/// queued but not run would sit in a lane for ever, blocking everything behind it.
	/// </remarks>
	public static IReadOnlySet<WorkKind> SupportedKinds { get; } = new HashSet<WorkKind>
	{
		WorkKind.Clone, WorkKind.Reassess, WorkKind.FixAll, WorkKind.FixCategory, WorkKind.FixRule,
		WorkKind.Build, WorkKind.Test, WorkKind.GitSync, WorkKind.CommitAndPush, WorkKind.Publish,
		WorkKind.RediscoverOrganization, WorkKind.DiscoverReassessTargets,
		WorkKind.DiscoverCloneTargets, WorkKind.RefreshAll
	};

	/// <summary>Runs one queued item.</summary>
	/// <param name="item">The item to run; its <see cref="WorkItem.Descriptor"/> selects the body.</param>
	/// <param name="progress">Reports progress lines into the item's tree node.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	public Task ExecuteAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
		=> item.Descriptor.Kind switch
		{
			WorkKind.Build => BuildAsync(item, progress, cancellationToken),
			WorkKind.Test => TestAsync(item, progress, cancellationToken),
			WorkKind.GitSync => GitSyncAsync(item, progress, cancellationToken),
			WorkKind.CommitAndPush => CommitAndPushAsync(item, progress, cancellationToken),
			WorkKind.Publish => PublishAsync(item, progress, cancellationToken),
			_ => throw new NotSupportedException($"No executor for {item.Descriptor.Kind}.")
		};

	/// <summary>
	/// Says something in the console the item was queued from.
	/// </summary>
	/// <remarks>
	/// Logged rather than written into a buffer: the work has no console of its own, and the UI
	/// console subscribes to this category. The line is passed as an argument rather than as the
	/// template so braces in a path or a git message are not read as structured-logging placeholders.
	/// </remarks>
	/// <param name="line">The line to say.</param>
	private void Say(string line) => logger.LogInformation("{ConsoleLine}", line);

	/// <summary>
	/// Notes that a restored item names a repository the estate no longer has, so its body is skipped.
	/// </summary>
	/// <param name="item">The item that cannot run.</param>
	private void SayRepositoryGone(WorkItem item)
		=> logger.LogWarning(
			"Skipping {Title}: {Repository} is no longer in the estate.",
			item.Title,
			item.RepositoryFullName);

	/// <summary>
	/// Builds one repository, and on failure leaves behind the AI prompt for the failure.
	/// </summary>
	/// <remarks>
	/// The prompt used to be copied to the clipboard and an IDE opened alongside it. Twenty lanes
	/// finishing together would race twenty of each, so it is stored on the item and claimed from the
	/// UI instead. The console lines it quotes are the ones this build itself produced, rather than
	/// whatever happened to be in a shared console buffer.
	/// </remarks>
	/// <param name="item">The item naming the repository to build.</param>
	/// <param name="progress">Unused: a build reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task BuildAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var output = new List<string>();
		Say("▶ Building...");

		try
		{
			await dashboard.BuildAsync(
				row,
				line =>
				{
					output.Add(line);
					Say(line);
				},
				cancellationToken);

			if (row.Status == PackageStatus.BuildSucceeded)
			{
				Say("✅ Build succeeded");
			}
			else
			{
				Say("❌ Build failed");
				item.GeneratedPrompt = DashboardService.GenerateConciseWorkflowFailurePrompt(row, "build", output);
			}
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// The cached row for an item's repository, or null when the repository is no longer known —
	/// which happens when a restored item names a repository since removed from the estate.
	/// </summary>
	/// <param name="item">The item whose repository is wanted.</param>
	private RepositoryDashboardRow? RowFor(WorkItem item)
		=> cache.GetCachedRows()?.FirstOrDefault(r => string.Equals(
			r.RepositoryFullName, item.RepositoryFullName, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Runs one repository's tests, and on failure leaves behind the AI prompt for the failure.
	/// </summary>
	/// <remarks>
	/// Like the build, the prompt is stored rather than pushed at a browser, and quotes only the
	/// lines this run produced.
	/// </remarks>
	/// <param name="item">The item naming the repository to test.</param>
	/// <param name="progress">Unused: a test run reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task TestAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		var output = new List<string>();
		Say("▶ Running tests...");

		try
		{
			await dashboard.RunTestsAsync(
				row,
				line =>
				{
					output.Add(line);
					Say(line);
				},
				cancellationToken);

			if (row.Status == PackageStatus.TestsPassed)
			{
				Say("✅ Tests passed");
			}
			else
			{
				Say("❌ Tests failed");
				item.GeneratedPrompt = DashboardService.GenerateConciseWorkflowFailurePrompt(row, "test", output);
			}
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Pulls and pushes one repository, then reports where its working tree stands.
	/// </summary>
	/// <param name="item">The item naming the repository to sync.</param>
	/// <param name="progress">Unused: a sync reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task GitSyncAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		Say("▶ Git syncing...");

		try
		{
			await dashboard.GitSyncAsync(row, Say, cancellationToken);

			Say(row.Status == PackageStatus.GitSynced ? "✅ Git sync complete" : "❌ Git sync failed");
			cache.UpsertRow(row);

			if (row.CurrentBranch is not null)
			{
				Say($"ℹ️ Branch: {row.CurrentBranch} | Clean: {row.IsWorkingTreeClean} | Synced: {row.IsSyncedWithOrigin}");
			}

			await SayDirtyWorkingTreePreviewAsync(row);
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Publishes one repository's packages.
	/// </summary>
	/// <param name="item">The item naming the repository to publish.</param>
	/// <param name="progress">Unused: a publish reports no sub-steps.</param>
	/// <param name="cancellationToken">Signalled when the user stops the item.</param>
	private async Task PublishAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		Say("▶ Publishing...");

		try
		{
			await dashboard.RunPublishAsync(row, Say, cancellationToken);

			Say(row.Status == PackageStatus.Published ? "✅ Published" : "❌ Publish failed");
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Commits and pushes one repository's working tree.
	/// </summary>
	/// <remarks>
	/// A guard's refusal used to be raised as a dialog, on the grounds that a console can be
	/// collapsed. There is no dialog to raise from a lane — and twenty lanes would raise twenty — so
	/// it is logged as a warning instead, which is the loudest thing the console has.
	/// </remarks>
	/// <param name="item">The item naming the repository to commit and push.</param>
	/// <param name="progress">Unused: a commit and push reports no sub-steps.</param>
	/// <param name="cancellationToken">Unused: the underlying git calls are not interruptible.</param>
	private async Task CommitAndPushAsync(WorkItem item, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var row = RowFor(item);
		if (row is null)
		{
			SayRepositoryGone(item);
			return;
		}

		Say("▶ Committing and pushing...");

		try
		{
			var commitMessage = $"NuGet governance remediation for {row.RepositoryFullName}";
			var push = await dashboard.CommitAndPushAsync(row, commitMessage, Say);

			if (push.Success)
			{
				Say("✅ Changes committed and pushed");
				cache.UpsertRow(row);

				if (row.CurrentBranch is not null)
				{
					Say($"ℹ️ Branch: {row.CurrentBranch} | Clean: {row.IsWorkingTreeClean} | Synced: {row.IsSyncedWithOrigin}");
				}
			}
			else
			{
				Say("❌ Commit and push failed");
				cache.UpsertRow(row);

				if (push.WasRefused)
				{
					logger.LogWarning(
						"Nothing was pushed for {Repository}: {Reason}",
						row.RepositoryFullName,
						push.RefusalReason);
				}
			}
		}
		catch (Exception ex)
		{
			Say($"❌ Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Names the first few uncommitted files when a working tree is dirty, and says nothing at all
	/// when it is clean.
	/// </summary>
	/// <remarks>
	/// A dirty tree is what stops a fix being pushed, so the reason is put in front of the user at
	/// the moment it is discovered rather than left for them to go and find with git.
	/// </remarks>
	/// <param name="row">The repository whose working tree to describe.</param>
	private async Task SayDirtyWorkingTreePreviewAsync(RepositoryDashboardRow row)
	{
		if (row.IsWorkingTreeClean != false)
		{
			return;
		}

		var lines = await dashboard.GetWorkingTreeStatusPreviewAsync(row, maxLines: 3);
		if (lines.Count == 0)
		{
			Say("ℹ️ Working tree is dirty but no git porcelain entries were captured.");
			return;
		}

		Say("ℹ️ Dirty files (git status --porcelain):");
		foreach (var line in lines)
		{
			Say($"  {line}");
		}
	}

	/// <summary>
	/// A GitHub client carrying the signed-in user's token when one can be read.
	/// </summary>
	/// <remarks>
	/// The token belongs to a sign-in, and work no longer belongs to the browser tab that started
	/// it: by the time a lane reaches an item there may be no request in flight at all. When the
	/// token cannot be read the client is anonymous, which still answers for public repositories but
	/// against a far smaller rate limit — so the shortfall is logged rather than passed off as
	/// normal.
	/// </remarks>
	private async Task<IGitHubClient> CreateGitHubClientAsync()
	{
		var client = new GitHubClient(new ProductHeaderValue("PanoramicData.NugetManagement.Web"));

		var httpContext = httpContextAccessor.HttpContext;
		if (httpContext is null)
		{
			logger.LogWarning(
				"No signed-in GitHub token was available; falling back to anonymous GitHub access, "
				+ "which cannot see private repositories and has a much smaller rate limit.");
			return client;
		}

		var accessToken = await httpContext.GetTokenAsync("access_token");
		if (accessToken is not null)
		{
			client.Credentials = new Credentials(accessToken);
		}

		return client;
	}
}
