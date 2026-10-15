using PanoramicData.NugetManagement.Web.Services;
using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="GatedAiChangeWork"/>: the model's server is held while the model works, and not
/// a moment longer.
/// </summary>
public sealed class GatedAiChangeWorkTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly OllamaGate _gate = new(() => 1);

	private static readonly AiChangeRequest Request = new("o", "r", AiChangeKind.IssueFix, "issue-1-a", "Fix #1", 1, true);

	public void Dispose()
	{
		_gate.Dispose();
		GC.SuppressFinalize(this);
	}

	/// <summary>An inner work that says when it has started and waits to be told to finish.</summary>
	private sealed class BlockingWork : IAiChangeWork
	{
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<string?> WriteRedTestAsync(AiChangeRequest request, string? feedback, CancellationToken cancellationToken)
		{
			Started.TrySetResult();
			await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			return "FILTER";
		}

		public async Task<AiFixOutcome> FixAsync(AiChangeRequest request, string? redTestFilter, CancellationToken cancellationToken)
		{
			Started.TrySetResult();
			await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			return new AiFixOutcome(true, 1, "done");
		}
	}

	private async Task<bool> GateIsFreeAsync()
	{
		using var brief = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		brief.CancelAfter(TimeSpan.FromMilliseconds(200));

		try
		{
			using var hold = await _gate.EnterAsync(brief.Token).ConfigureAwait(true);
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
	}

	[Fact]
	public async Task WriteRedTestAsync_HoldsTheGateWhileTheModelWorks_AndReleasesItAfter()
	{
		var inner = new BlockingWork();
		var work = new GatedAiChangeWork(inner, _gate);

		var running = work.WriteRedTestAsync(Request, null, TestContext.Current.CancellationToken);
		await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

		(await GateIsFreeAsync()).Should().BeFalse("the model is mid-call, so nobody else may be talking to it");

		inner.Release.SetResult();
		await running.ConfigureAwait(true);

		(await GateIsFreeAsync()).Should().BeTrue("the call is over, and a CI wait after it must not hold the model's server");
	}

	[Fact]
	public async Task FixAsync_HoldsTheGateWhileTheModelWorks_AndReleasesItAfter()
	{
		var inner = new BlockingWork();
		var work = new GatedAiChangeWork(inner, _gate);

		var running = work.FixAsync(Request, "FILTER", TestContext.Current.CancellationToken);
		await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

		(await GateIsFreeAsync()).Should().BeFalse();

		inner.Release.SetResult();
		(await running.ConfigureAwait(true)).Succeeded.Should().BeTrue();

		(await GateIsFreeAsync()).Should().BeTrue();
	}

	[Fact]
	public async Task AFailingCall_StillReleasesTheGate()
	{
		var inner = new BlockingWork();
		var work = new GatedAiChangeWork(inner, _gate);
		using var stop = new CancellationTokenSource();

		var running = work.FixAsync(Request, null, stop.Token);
		await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

		await stop.CancelAsync();
		var act = () => running;
		await act.Should().ThrowAsync<OperationCanceledException>();

		(await GateIsFreeAsync()).Should().BeTrue("a stop must not leave the model's server held for good");
	}
}
