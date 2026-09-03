using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for what the code is allowed to conclude from a 404 on a Codacy repository-scoped listing.
/// </summary>
/// <remarks>
/// Codacy answered the file listing for panoramicdata/ConnectWise.Manage.Api with a 404 while
/// holding the repository — added 2026-09-01, default branch main, enabled — and CQ-03 reported
/// "Codacy does not know this repository — it has not been added". Six of the eleven repositories
/// that failed that way in one sweep answered 200 an hour later, all six of them the batch added on
/// 2026-09-01. A listing 404 therefore does not establish absence, and only the repository endpoint
/// answering 404 for the same name does.
/// <para>
/// That endpoint is case-sensitive — Dell.CloudIQ.Api 404s where Dell.CloudIq.Api does not — so
/// corroborating through it keeps the wrong-casing defect visible rather than tolerating it, which
/// is what the reverted case-insensitive fallback got wrong.
/// </para>
/// </remarks>
public class CodacyTrackingTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _now = new(2026, 9, 3, 10, 41, 42, TimeSpan.Zero);

	[Fact]
	public async Task ShouldReportNotAdded_WhenTheRepositoryEndpointAlsoAnswers404()
	{
		// The one case that establishes absence: Codacy holds nothing under this exact name.
		var retried = false;

		var state = await CodacyTracking.ResolveAsync(
			isAddedAsync: _ => Task.FromResult(false),
			retryListingAsync: _ => { retried = true; return Task.FromResult(true); },
			window: new CodacyRetryWindow(TimeSpan.FromHours(1)),
			key: "acme/Absent",
			now: _now,
			cancellationToken: TestContext.Current.CancellationToken);

		state.Should().Be(CodacyTrackingState.NotAdded);
		retried.Should().BeFalse("a repository Codacy does not hold will not start answering on a retry");
	}

	[Fact]
	public async Task ShouldRetryTheListing_WhenTheRepositoryIsThereAfterAll()
	{
		// The 404 was about something other than absence, so the listing is worth asking again.
		var attempts = 0;

		var state = await CodacyTracking.ResolveAsync(
			isAddedAsync: _ => Task.FromResult(true),
			retryListingAsync: _ => { attempts++; return Task.FromResult(true); },
			window: new CodacyRetryWindow(TimeSpan.FromHours(1)),
			key: "acme/Present",
			now: _now,
			cancellationToken: TestContext.Current.CancellationToken);

		state.Should().Be(CodacyTrackingState.Listed);
		attempts.Should().Be(1);
	}

	[Fact]
	public async Task ShouldReportAddedButNotListed_WhenTheRetry404sAsWell()
	{
		// Added to Codacy with nothing listed for the branch. CQ-03 has a sentence for exactly this
		// state, and it is the one the reader can act on.
		var state = await CodacyTracking.ResolveAsync(
			isAddedAsync: _ => Task.FromResult(true),
			retryListingAsync: _ => Task.FromResult(false),
			window: new CodacyRetryWindow(TimeSpan.FromHours(1)),
			key: "acme/Unanalysed",
			now: _now,
			cancellationToken: TestContext.Current.CancellationToken);

		state.Should().Be(CodacyTrackingState.AddedButNotListed);
	}

	[Fact]
	public async Task ShouldNotRetryTwiceWithinTheHour()
	{
		// Three rules ask for the same repository in one sweep. One retry an hour is the whole budget,
		// and the repositories that need it are the ones a retry cannot help quickly anyway.
		var window = new CodacyRetryWindow(TimeSpan.FromHours(1));
		var attempts = 0;

		Task<CodacyTrackingState> Resolve(DateTimeOffset now) => CodacyTracking.ResolveAsync(
			isAddedAsync: _ => Task.FromResult(true),
			retryListingAsync: _ => { attempts++; return Task.FromResult(false); },
			window: window,
			key: "acme/Unanalysed",
			now: now,
			cancellationToken: TestContext.Current.CancellationToken);

		await Resolve(_now);
		await Resolve(_now.AddMinutes(30));
		await Resolve(_now.AddMinutes(59));

		attempts.Should().Be(1, "the second and third asks fall inside the hour");
		(await Resolve(_now.AddMinutes(30))).Should().Be(
			CodacyTrackingState.AddedButNotListed,
			"a skipped retry still reports the repository as present");
	}

	[Fact]
	public async Task ShouldRetryAgain_OnceTheHourHasPassed()
	{
		var window = new CodacyRetryWindow(TimeSpan.FromHours(1));
		var attempts = 0;

		Task<CodacyTrackingState> Resolve(DateTimeOffset now) => CodacyTracking.ResolveAsync(
			isAddedAsync: _ => Task.FromResult(true),
			retryListingAsync: _ => { attempts++; return Task.FromResult(false); },
			window: window,
			key: "acme/Unanalysed",
			now: now,
			cancellationToken: TestContext.Current.CancellationToken);

		await Resolve(_now);
		await Resolve(_now.AddHours(1).AddSeconds(1));

		attempts.Should().Be(2);
	}

	[Fact]
	public async Task ShouldBudgetEachRepositorySeparately()
	{
		// A sweep of eighty repositories must not spend one repository's retry on another's behalf.
		var window = new CodacyRetryWindow(TimeSpan.FromHours(1));
		var attempts = new List<string>();

		Task<CodacyTrackingState> Resolve(string key) => CodacyTracking.ResolveAsync(
			isAddedAsync: _ => Task.FromResult(true),
			retryListingAsync: _ => { attempts.Add(key); return Task.FromResult(false); },
			window: window,
			key: key,
			now: _now,
			cancellationToken: TestContext.Current.CancellationToken);

		await Resolve("acme/One");
		await Resolve("acme/Two");
		await Resolve("acme/One");

		attempts.Should().Equal("acme/One", "acme/Two");
	}

	[Fact]
	public void ShouldKeepTheWholeHourForOneCaller_WhenManyThreadsAskAtOnce()
	{
		// Every rule in a parallel sweep reaches the window at once, and a check-then-record that is
		// not atomic hands all of them the retry.
		var window = new CodacyRetryWindow(TimeSpan.FromHours(1));
		var granted = 0;

		Parallel.For(0, 64, _ =>
		{
			if (window.TryBeginRetry("acme/One", _now))
			{
				Interlocked.Increment(ref granted);
			}
		});

		granted.Should().Be(1);
	}
}
