using System.Collections.Concurrent;
using System.Threading.Channels;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// A background build queue that verifies a repository still compiles and reports whether our commits
/// are proven to have broken the build. It only reports; it never reverts or pushes. Repositories are
/// built in the background with bounded concurrency so the UI stays responsive.
/// </summary>
public sealed class RegressionGuardService(
	LocalRepoService localRepo,
	ILogger<RegressionGuardService> logger) : BackgroundService
{
	private const int MaxConcurrentBuilds = 2;

	private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
	private readonly ConcurrentDictionary<string, string> _lastVerified = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, RepoGuardStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Raised whenever a repository's guard status changes.</summary>
	public event Action? StatusesChanged;

	/// <summary>A snapshot of current guard statuses, most recently updated first.</summary>
	public IReadOnlyList<RepoGuardStatus> Statuses => [.. _statuses.Values.OrderByDescending(s => s.UpdatedUtc)];

	/// <summary>
	/// Queues a repository for build verification.
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
			catch (Exception ex) when (ex is not OperationCanceledException)
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
		var commits = await localRepo.GetRecentCommitsAsync(repositoryFullName, 30, cancellationToken).ConfigureAwait(false);
		if (build.Success)
		{
			if (commits.Count > 0)
			{
				_lastVerified[repositoryFullName] = commits[0].Hash;
			}

			SetStatus(repositoryFullName, GuardState.Verified, "Build succeeded after our change.");
			return;
		}

		// Build failed. Is it our change that broke it? Our commits are collected wherever they sit in
		// the range since the last green build, so a human commit on top does not hide them.
		_lastVerified.TryGetValue(repositoryFullName, out var lastVerified);
		var attribution = RegressionAttribution.Identify(commits, lastVerified);
		if (attribution.Ours.Count == 0 || attribution.LastGoodRef is null)
		{
			SetStatus(repositoryFullName, GuardState.BuildFailingNotOurs,
				"Build is failing, but none of the commits since the last good build are ours, so not rolling back.");
			return;
		}

		if (!attribution.LastGoodVerified
			&& !await ControlBuildsAsync(repositoryFullName, attribution, cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		await NameCulpritAsync(repositoryFullName, attribution, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Builds the control commit; a failing control is reported as inconclusive, not as exoneration.</summary>
	private async Task<bool> ControlBuildsAsync(string repositoryFullName, Attribution attribution, CancellationToken cancellationToken)
	{
		var lastGoodRef = attribution.LastGoodRef!;
		var parentBuild = await localRepo.BuildAtCommitAsync(repositoryFullName, lastGoodRef, null, cancellationToken).ConfigureAwait(false);
		if (parentBuild.Success)
		{
			return true;
		}

		// The control build failing does NOT prove the breakage predates us. It is equally
		// consistent with the toolchain being unavailable, or with a remediation that broke the
		// build at every commit - pinning global.json to an uninstalled SDK fails HEAD and the
		// control commit identically. Treating that as exoneration disables the guard exactly
		// when the damage is worst, so it is reported as inconclusive and left visible.
		logger.LogWarning(
			"Regression guard: {Repo} is failing and the control build at {LastGood} also failed; "
			+ "cannot attribute. Our {Count} commit(s) remain in place and need a human look.",
			repositoryFullName, lastGoodRef, attribution.Ours.Count);
		SetStatus(repositoryFullName, GuardState.VerificationInconclusive,
			$"Build is failing and the control build at {lastGoodRef} also failed, so it could not be "
			+ $"established whether our {attribution.Ours.Count} commit(s) caused it. They are still in place. "
			+ "Check the toolchain on this host before assuming the breakage predates them.");
		return false;
	}

	/// <summary>
	/// Bisects our commits to the first that fails, then checks its parent builds: when a human commit
	/// sits between, the parent failing means the breakage is theirs, not ours.
	/// </summary>
	private async Task NameCulpritAsync(string repositoryFullName, Attribution attribution, CancellationToken cancellationToken)
	{
		var (culprit, isOurs) = await RegressionAttribution
			.NameCulpritAsync(attribution, reference => BuildsAtAsync(repositoryFullName, reference, cancellationToken))
			.ConfigureAwait(false);
		if (!isOurs)
		{
			SetStatus(repositoryFullName, GuardState.BuildFailingNotOurs,
				$"The build already fails at the commit before {culprit.Hash} ('{culprit.Subject}'), "
				+ "which is not a commit of ours, so the breakage is not attributed to us. Nothing has been reverted.");
			return;
		}

		logger.LogWarning(
			"Regression guard: {Repo} fails to build from {Hash} ({Subject}); nothing reverted.",
			repositoryFullName, culprit.Hash, culprit.Subject);
		SetStatus(repositoryFullName, GuardState.RegressionDetected,
			$"Regression detected: the build first fails at commit {culprit.Hash} ('{culprit.Subject}'). "
			+ $"{attribution.Ours.Count} of our governance commit(s) sit above the last good build. Nothing has been reverted; "
			+ "revert or fix that commit by hand.");
	}

	private async Task<bool> BuildsAtAsync(string repositoryFullName, string reference, CancellationToken cancellationToken)
		=> (await localRepo.BuildAtCommitAsync(repositoryFullName, reference, null, cancellationToken).ConfigureAwait(false)).Success;

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
