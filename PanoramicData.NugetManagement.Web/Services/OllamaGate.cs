namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Limits how many AI fixes may be talking to the model at once.
/// </summary>
/// <remarks>
/// The repository lanes are the wrong place to bound this. They exist to keep one repository's work in
/// order, and they run twenty at a time by design; a single box serving a single model does not want
/// twenty callers. So the limit is here, read from the setting on every entry rather than captured
/// once, because changing "how many at a time" should not need a restart.
/// <para>
/// Deliberately a gate rather than a dedicated lane. A lane of its own would let an AI fix and an
/// ordinary Fix run against the same clone simultaneously — the repository lane is the only thing that
/// makes work on one working tree mutually exclusive, and that guarantee is worth more than keeping the
/// lane free while waiting.
/// </para>
/// </remarks>
public sealed class OllamaGate(Func<int> maxConcurrency) : IDisposable
{
	private readonly SemaphoreSlim _semaphore = new(0, int.MaxValue);
	private readonly Lock _lock = new();
	private int _issued;

	/// <summary>
	/// Waits for permission to call the model, and returns the hold to release.
	/// </summary>
	/// <param name="cancellationToken">Signalled when the user stops the work item.</param>
	public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
	{
		TopUp();

		await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

		return new Hold(this);
	}

	/// <summary>
	/// Releases permits up to the configured limit.
	/// </summary>
	/// <remarks>
	/// The semaphore starts empty and is filled to the current setting on demand, which is what lets the
	/// setting be raised while work is in flight. Lowering it does not revoke a permit already issued:
	/// the call holding it finishes, and the count settles at the new limit as holds are returned.
	/// </remarks>
	private void TopUp()
	{
		lock (_lock)
		{
			var wanted = Math.Max(1, maxConcurrency());

			if (_issued >= wanted)
			{
				return;
			}

			var shortfall = wanted - _issued;
			_issued = wanted;
			_semaphore.Release(shortfall);
		}
	}

	private void Return()
	{
		lock (_lock)
		{
			// A permit issued above the current limit is retired rather than returned, so that lowering
			// the setting takes effect as work drains instead of never.
			if (_issued > Math.Max(1, maxConcurrency()))
			{
				_issued--;
				return;
			}
		}

		_semaphore.Release();
	}

	/// <inheritdoc />
	public void Dispose() => _semaphore.Dispose();

	private sealed class Hold(OllamaGate gate) : IDisposable
	{
		private bool _released;

		public void Dispose()
		{
			if (_released)
			{
				return;
			}

			_released = true;
			gate.Return();
		}
	}
}
