using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="OllamaGate"/>: the thing that stops an estate-wide sweep flattening one GPU.
/// </summary>
public class OllamaGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task WithAGateOfOne_OnlyOneSessionRunsAtATime()
	{
		var gate = new OllamaGate(() => 1);
		var concurrent = 0;
		var peak = 0;

		await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
		{
			using var _held = await gate
				.EnterAsync(TestContext.Current.CancellationToken)
				.ConfigureAwait(false);

			var now = Interlocked.Increment(ref concurrent);
			InterlockedMax(ref peak, now);
			await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(false);
			Interlocked.Decrement(ref concurrent);
		})).ConfigureAwait(true);

		peak.Should().Be(1, "the default exists because one box serving one model wants one caller");
	}

	[Fact]
	public async Task WithAGateOfThree_NoMoreThanThreeRunAtOnce()
	{
		var gate = new OllamaGate(() => 3);
		var concurrent = 0;
		var peak = 0;

		await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
		{
			using var _held = await gate
				.EnterAsync(TestContext.Current.CancellationToken)
				.ConfigureAwait(false);

			var now = Interlocked.Increment(ref concurrent);
			InterlockedMax(ref peak, now);
			await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(false);
			Interlocked.Decrement(ref concurrent);
		})).ConfigureAwait(true);

		peak.Should().BeLessThanOrEqualTo(3);
		peak.Should().BeGreaterThan(1, "a gate that serialises everything regardless of setting is not a gate");
	}

	[Fact]
	public async Task ReleasingLetsTheNextWaiterIn()
	{
		var gate = new OllamaGate(() => 1);

		using (await gate.EnterAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
		{
			// held
		}

		// Would deadlock if the first hold had not been released.
		using var second = await gate
			.EnterAsync(TestContext.Current.CancellationToken)
			.ConfigureAwait(true);

		second.Should().NotBeNull();
	}

	[Fact]
	public async Task RaisingTheSettingLater_LetsMoreThrough()
	{
		var permitted = 1;
		var gate = new OllamaGate(() => permitted);

		using (await gate.EnterAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
		{
			permitted = 2;

			// The second permit only exists because the setting was raised while the first was held.
			using var second = await gate
				.EnterAsync(TestContext.Current.CancellationToken)
				.ConfigureAwait(true);

			second.Should().NotBeNull("changing the setting must not require a restart");
		}
	}

	[Fact]
	public async Task ADeniedGate_IsCancellable()
	{
		var gate = new OllamaGate(() => 1);
		using var held = await gate.EnterAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync().ConfigureAwait(true);

		var act = async () => await gate.EnterAsync(cancellation.Token).ConfigureAwait(true);

		await act.Should().ThrowAsync<OperationCanceledException>(
			"stopping a queued AI item must not wait for the GPU to free up").ConfigureAwait(true);
	}

	private static void InterlockedMax(ref int target, int value)
	{
		var current = Volatile.Read(ref target);

		while (value > current)
		{
			var seen = Interlocked.CompareExchange(ref target, value, current);

			if (seen == current)
			{
				return;
			}

			current = seen;
		}
	}
}
