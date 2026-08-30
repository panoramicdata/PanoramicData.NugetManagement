using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Runs the lanes. Owned by the application rather than by any browser tab, so work outlives the
/// tab that asked for it and, through <see cref="WorkQueueStore"/>, the process itself.
/// </summary>
/// <remarks>
/// The queue used to be pumped by the Blazor circuit that enqueued it, which meant closing the tab
/// cancelled the work. That was tolerable when one item ran at a time; with twenty lanes in flight
/// it is not.
/// </remarks>
public sealed class WorkRunnerService(
	WorkLaneService lanes,
	WorkQueueStore store,
	IServiceScopeFactory scopeFactory,
	ILogger<WorkRunnerService> logger) : BackgroundService
{
	private readonly SemaphoreSlim _wake = new(0);

	/// <summary>Raised when an item finishes, so open circuits can refresh what it changed.</summary>
	public event Action<WorkItem>? ItemCompleted;

	/// <inheritdoc />
	public override Task StartAsync(CancellationToken cancellationToken)
	{
		// Restored before the pump starts, so work saved by the last run is in its lanes by the time
		// anything can claim it.
		lanes.Restore(store.Load());

		// The pump reacts only to QueueChanged, never to Changed: Changed also fires for a progress
		// report, and a progress line is not a change to what is queued — saving on every one of up
		// to twenty lanes' progress reports would rewrite the queue file continuously for no reason,
		// and would wake the pump for nothing it could act on.
		lanes.QueueChanged += OnQueueChanged;
		return base.StartAsync(cancellationToken);
	}

	/// <inheritdoc />
	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		// Unsubscribed before the final save, so a queue change racing shutdown cannot trigger a
		// second, overlapping save of a lanes snapshot that is itself being torn down.
		lanes.QueueChanged -= OnQueueChanged;
		store.Save(lanes.Snapshot());
		await base.StopAsync(cancellationToken);
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			// Claims as many as the cap allows, then sleeps until something changes. Each claimed item
			// runs on its own task: that is the concurrency, and TryStartNext is what bounds it.
			while (lanes.TryStartNext(out var item))
			{
				_ = RunAsync(item);
			}

			try
			{
				await _wake.WaitAsync(stoppingToken);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	private void OnQueueChanged()
	{
		store.Save(lanes.Snapshot());

		// Released rather than set: a change while the pump is mid-claim must not be lost, or a lane
		// would sit ready with nothing to wake it.
		if (_wake.CurrentCount == 0)
		{
			_wake.Release();
		}
	}

	private async Task RunAsync(WorkItem item)
	{
		Exception? error = null;

		// Stamped on this method's asynchronous flow, which the work and everything it logs runs
		// inside. That, not any current selection, is what decides where its output appears — and it
		// is why output still reaches the right console when no tab started it.
		UiConsoleScope.NodeKey = item.ConsoleNodeKey;

		using var scope = scopeFactory.CreateScope();
		var executors = scope.ServiceProvider.GetRequiredService<WorkExecutors>();
		var localRepo = scope.ServiceProvider.GetRequiredService<LocalRepoService>();

		try
		{
			// An item that was executing when the process stopped may have left the clone half-written.
			// Cleaned before it runs again, for the same reason cancellation cleans up: a half-applied
			// fix must not be built on. CancellationToken.None: this cleanup must not itself be
			// cancellable, since an item stopped after this point but before it starts would otherwise
			// be resumed on a working tree left half-reverted.
			if (item.WasInterrupted && item.RepositoryFullName is { Length: > 0 } repository)
			{
				var (success, discarded) = await localRepo.DiscardLocalChangesAsync(repository, CancellationToken.None);
				if (success && discarded.Count > 0)
				{
					logger.LogInformation(
						"↩️ Reverted {Count} change(s) left by {Title} when the application last stopped.",
						discarded.Count,
						item.Title);
				}
			}

			var progress = new Progress<string>(line => lanes.ReportProgress(item, line));
			await executors.ExecuteAsync(item, progress, lanes.Token(item.Id) ?? CancellationToken.None);
		}
		catch (OperationCanceledException ex)
		{
			error = ex;
			logger.LogInformation("⏹️ Stopped: {Title}", item.Title);
		}
		catch (Exception ex)
		{
			error = ex;
			logger.LogError(ex, "⛔ {Title}: {Message}", item.Title, ex.Message);
		}
		finally
		{
			// Reached on every exit path — including when ExecuteAsync throws synchronously before
			// its first await — so an item can never fall out of its lane without freeing it.
			lanes.Complete(item, error);
			ItemCompleted?.Invoke(item);
		}
	}

	/// <inheritdoc />
	public override void Dispose()
	{
		_wake.Dispose();
		base.Dispose();
	}
}
