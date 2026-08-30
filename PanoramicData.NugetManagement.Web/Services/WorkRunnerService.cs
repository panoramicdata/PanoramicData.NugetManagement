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

	/// <summary>
	/// <see cref="Environment.TickCount64"/> when the queue first changed without having been saved
	/// since, or zero when the file on disk is current.
	/// </summary>
	/// <remarks>
	/// Set once per dirty window and never pushed forward by later changes, so the pump saves within
	/// <see cref="SaveDelayMilliseconds"/> of the <em>first</em> unsaved change however many follow it.
	/// A trailing debounce would instead be reset by each one, and a sweep that enqueues hundreds of
	/// items in a loop would postpone the save until the loop ended.
	/// </remarks>
	private long _queueDirtySinceTicks;

	/// <summary>
	/// How long unsaved queue changes may accumulate before the pump writes them.
	/// </summary>
	/// <remarks>
	/// The queue file used to be rewritten synchronously on whichever thread raised the change —
	/// which, for an enqueue, is the Blazor circuit. A forty-repository, twelve-rule sweep enqueues
	/// some five hundred items in one loop, so that was five hundred indented-JSON serialisations and
	/// blocking file writes of a growing list, on the UI thread, each one taking the lane lock that up
	/// to twenty runner threads are already contending. The circuit visibly hung.
	/// <para>
	/// Persistence has to be current-ish, not current per item: what it protects is the pending queue
	/// across a crash, and a quarter of a second of queueing is an acceptable thing to lose. Shutdown
	/// still writes a final synchronous snapshot, so an orderly stop loses nothing at all.
	/// </para>
	/// </remarks>
	private const int SaveDelayMilliseconds = 250;

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

		// Unconditionally, not only when dirty: this is the write that makes an orderly shutdown lose
		// nothing, whatever the pump did or did not get round to saving.
		Interlocked.Exchange(ref _queueDirtySinceTicks, 0);
		SaveQueue();
		await base.StopAsync(cancellationToken).ConfigureAwait(false);
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

			// Saved here rather than from whoever raised the change, so that a circuit enqueueing in a
			// loop is never the thread doing the writing, and so that a burst of changes costs one
			// write instead of one write each.
			var dirtySinceTicks = Interlocked.Read(ref _queueDirtySinceTicks);
			if (dirtySinceTicks != 0 && Environment.TickCount64 - dirtySinceTicks >= SaveDelayMilliseconds)
			{
				// Cleared before the snapshot is taken, not after it is written: a change arriving
				// during the write belongs to the next window, and clearing afterwards would drop it.
				Interlocked.Exchange(ref _queueDirtySinceTicks, 0);
				SaveQueue();
				continue;
			}

			try
			{
				// Bounded only while something is waiting to be saved. Idle, the pump sleeps until it
				// is woken rather than polling for a save it knows is not due.
				var timeout = dirtySinceTicks == 0
					? Timeout.InfiniteTimeSpan
					: TimeSpan.FromMilliseconds(SaveDelayMilliseconds);

				await _wake.WaitAsync(timeout, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	/// <summary>
	/// Writes the queue, treating a failure as something to log rather than something to stop the pump.
	/// </summary>
	private void SaveQueue()
	{
		try
		{
			store.Save(lanes.Snapshot());
		}
		catch (Exception ex)
		{
			// The store already swallows its own IO failures; this is the belt to that braces, because
			// an exception escaping here would fault the BackgroundService and, under the default
			// StopHost behaviour, shut the web application down over a queue file.
			logger.LogError(ex, "Failed to save the work queue.");
		}
	}

	private void OnQueueChanged()
	{
		// Marked, not written. The queue file used to be rewritten synchronously right here — on the
		// circuit thread, for every one of the hundreds of items a bulk sweep enqueues in a loop. The
		// pump writes it instead, at most once per SaveDelayMilliseconds. CompareExchange rather than
		// a plain store, so the window is timed from the first unsaved change and cannot be pushed
		// out indefinitely by a stream of later ones.
		Interlocked.CompareExchange(ref _queueDirtySinceTicks, Environment.TickCount64, 0);

		// Released rather than set: a change while the pump is mid-claim must not be lost, or a lane
		// would sit ready with nothing to wake it. This check-then-release is a wake-up hint, not a
		// strict gate: two concurrent QueueChanged events can both observe zero and both release,
		// taking the count above one. That is harmless — the claim loop above is self-correcting, and
		// a spurious extra wake just finds nothing to claim and re-blocks — so it costs one no-op pass,
		// never a missed one.
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

		try
		{
			// Scope creation and resolution live inside the try, not above it: a DI failure here — a
			// missing or misconfigured registration, exactly the sort of thing a later wiring task
			// could get wrong — would otherwise skip the finally below, leave the item stuck Running
			// for ever, strand its lane, and vanish as an unobserved exception on this fire-and-forget
			// task with nothing logged and nothing visible to the user.
			using var scope = scopeFactory.CreateScope();
			var executors = scope.ServiceProvider.GetRequiredService<WorkExecutors>();
			var localRepo = scope.ServiceProvider.GetRequiredService<LocalRepoService>();

			// An item that was executing when the process stopped may have left the clone half-written.
			// Cleaned before it runs again, for the same reason cancellation cleans up: a half-applied
			// fix must not be built on. CancellationToken.None: this cleanup must not itself be
			// cancellable, since an item stopped after this point but before it starts would otherwise
			// be resumed on a working tree left half-reverted.
			if (item.WasInterrupted && item.RepositoryFullName is { Length: > 0 } repository)
			{
				var (success, discarded) = await localRepo.DiscardLocalChangesAsync(repository, CancellationToken.None).ConfigureAwait(false);
				if (success && discarded.Count > 0)
				{
					logger.LogInformation(
						"↩️ Reverted {Count} change(s) left by {Title} when the application last stopped.",
						discarded.Count,
						item.Title);
				}
			}

			var progress = new Progress<string>(line => lanes.ReportProgress(item, line));
			await executors.ExecuteAsync(item, progress, lanes.Token(item.Id) ?? CancellationToken.None).ConfigureAwait(false);
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
			// Reached on every exit path — including a synchronous throw from ExecuteAsync, and a
			// throw from scope creation or resolution above — so an item can never fall out of its
			// lane without freeing it.
			lanes.Complete(item, error);

			try
			{
				ItemCompleted?.Invoke(item);
			}
			catch (Exception ex)
			{
				// A subscriber that throws must not take the runner down with it: the item is already
				// complete and its lane already free, so there is nothing left to unwind.
				logger.LogError(ex, "A completion subscriber threw for {Title}.", item.Title);
			}
		}
	}

	/// <inheritdoc />
	public override void Dispose()
	{
		_wake.Dispose();
		base.Dispose();
	}
}
