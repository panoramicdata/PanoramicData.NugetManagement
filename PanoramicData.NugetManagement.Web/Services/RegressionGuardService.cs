using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// A background build queue that verifies every repository we push a change to still compiles, and
/// automatically rolls back (reverts + pushes) our commits when — and only when — they are proven to
/// have broken the build. Repositories are built in the background with bounded concurrency so the UI
/// stays responsive.
/// </summary>
public sealed class RegressionGuardService(
	LocalRepoService localRepo,
	ILogger<RegressionGuardService> logger) : BackgroundService
{
	private const int MaxConcurrentBuilds = 2;
	private const string RevertMessage = "revert: auto-rollback governance remediation (build regression)";

	private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
	private readonly ConcurrentDictionary<string, RepoGuardStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Raised whenever a repository's guard status changes.</summary>
	public event Action? StatusesChanged;

	/// <summary>A snapshot of current guard statuses, most recently updated first.</summary>
	public IReadOnlyList<RepoGuardStatus> Statuses => [.. _statuses.Values.OrderByDescending(s => s.UpdatedUtc)];

	/// <summary>
	/// Queues a repository for build verification after we have pushed a change to it.
	/// </summary>
	public void Enqueue(string repositoryFullName)
	{
		if (string.IsNullOrWhiteSpace(repositoryFullName))
		{
			return;
		}

		SetStatus(repositoryFullName, GuardState.Queued, "Queued for build verification.");
		_queue.Writer.TryWrite(repositoryFullName);
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var workers = Enumerable
			.Range(0, MaxConcurrentBuilds)
			.Select(_ => Task.Run(() => WorkerAsync(stoppingToken), stoppingToken));
		await Task.WhenAll(workers).ConfigureAwait(false);
	}

	private async Task WorkerAsync(CancellationToken cancellationToken)
	{
		await foreach (var repositoryFullName in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
		{
			try
			{
				await VerifyAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Regression guard failed for {Repo}", repositoryFullName);
				SetStatus(repositoryFullName, GuardState.Error, ex.Message);
			}
		}
	}

	private async Task VerifyAsync(string repositoryFullName, CancellationToken cancellationToken)
	{
		SetStatus(repositoryFullName, GuardState.Building, "Building…");

		var build = await localRepo.BuildWithRestoreAsync(repositoryFullName, null, cancellationToken).ConfigureAwait(false);
		if (build.Success)
		{
			SetStatus(repositoryFullName, GuardState.Verified, "Build succeeded after our change.");
			return;
		}

		// Build failed — is it our change that broke it?
		var commits = await localRepo.GetRecentCommitsAsync(repositoryFullName, 30, cancellationToken).ConfigureAwait(false);
		var (ourCount, lastGoodRef) = RegressionAttribution.Identify(commits);
		if (ourCount == 0 || lastGoodRef is null)
		{
			SetStatus(repositoryFullName, GuardState.BuildFailingNotOurs,
				"Build is failing, but the tip commit is not ours — not rolling back.");
			return;
		}

		// Build the last-good commit to confirm our commits are the cause.
		var parentBuild = await localRepo.BuildAtCommitAsync(repositoryFullName, lastGoodRef, null, cancellationToken).ConfigureAwait(false);
		if (!parentBuild.Success)
		{
			SetStatus(repositoryFullName, GuardState.BuildFailingNotOurs,
				$"Build is failing, but it was already broken before our {ourCount} commit(s) — left in place for investigation.");
			return;
		}

		// Confirmed regression from our commits — revert them and push.
		logger.LogWarning("Regression guard: our {Count} commit(s) broke {Repo}; reverting.", ourCount, repositoryFullName);
		var revert = await localRepo.RevertRangeAndPushAsync(repositoryFullName, lastGoodRef, RevertMessage, null, cancellationToken).ConfigureAwait(false);
		SetStatus(repositoryFullName,
			revert.Success ? GuardState.RegressionReverted : GuardState.Error,
			revert.Success
				? $"Regression detected — reverted {ourCount} of our commit(s) and pushed. The build is green again; investigate the reverted change (it is a bug in our remediation)."
				: $"Regression detected but the automatic revert failed: {Truncate(revert.Output)}");
	}

	private static string Truncate(string value)
		=> string.IsNullOrEmpty(value) || value.Length <= 300 ? value : value[..300] + "…";

	private void SetStatus(string repositoryFullName, GuardState state, string message)
	{
		var status = _statuses.AddOrUpdate(
			repositoryFullName,
			_ => new RepoGuardStatus { RepositoryFullName = repositoryFullName },
			(_, existing) => existing);
		status.State = state;
		status.Message = message;
		status.UpdatedUtc = DateTimeOffset.UtcNow;
		StatusesChanged?.Invoke();
	}
}
