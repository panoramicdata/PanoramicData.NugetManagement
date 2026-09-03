using Codacy.Api.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="CodacyAnalysisStateLookup"/>, which asks Codacy where its analysis had got to
/// and is required never to cost the caller its grades.
/// </summary>
public class CodacyAnalysisStateLookupTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task ReturnsNull_WhenNeitherEndpointAnswers()
	{
		// The grades already arrived by the time this runs. Throwing here would turn a working finding
		// into "Codacy file grades could not be retrieved", which is a worse answer than an uncaveated
		// one — and null means "not established", which the rules render as silence.
		var state = await CodacyAnalysisStateLookup.ResolveAsync(
			Throws<FirstAnalysisOverview>(),
			Throws<Commit>(),
			_now,
			TestContext.Current.CancellationToken);

		state.Should().BeNull();
	}

	[Fact]
	public async Task ReturnsState_WhenTheProgressEndpointAnswersAndTheCommitDoesNot()
	{
		// Half an answer still tells the reader an analysis is running, which is the fact that matters
		// most. Requiring both would throw the useful half away.
		var state = await CodacyAnalysisStateLookup.ResolveAsync(
			_ => Task.FromResult<FirstAnalysisOverview?>(new FirstAnalysisOverview
			{
				IsFirstAnalysis = false,
				IsAnalyzing = true,
				Progress = 60
			}),
			Throws<Commit>(),
			_now,
			TestContext.Current.CancellationToken);

		state.Should().NotBeNull();
		state!.IsAnalysing.Should().BeTrue();
		state.AnalysedSha.Should().BeNull();
	}

	[Fact]
	public async Task ReturnsState_WhenTheCommitAnswersAndTheProgressEndpointDoesNot()
	{
		var state = await CodacyAnalysisStateLookup.ResolveAsync(
			Throws<FirstAnalysisOverview>(),
			_ => Task.FromResult<Commit?>(Commit("abc1234")),
			_now,
			TestContext.Current.CancellationToken);

		state.Should().NotBeNull();
		state!.AnalysedSha.Should().Be("abc1234");
		state.IsAnalysing.Should().BeFalse();
	}

	[Fact]
	public async Task RecordsWhenWeAsked()
	{
		var state = await CodacyAnalysisStateLookup.ResolveAsync(
			_ => Task.FromResult<FirstAnalysisOverview?>(null),
			_ => Task.FromResult<Commit?>(Commit("abc1234")),
			_now,
			TestContext.Current.CancellationToken);

		state!.RetrievedAtUtc.Should().Be(_now);
	}

	[Fact]
	public async Task PropagatesCancellation_RatherThanSwallowingIt()
	{
		// A cancelled assessment must stop, not quietly report an unknown freshness and carry on.
		var act = async () => await CodacyAnalysisStateLookup.ResolveAsync(
			_ => throw new OperationCanceledException(),
			_ => Task.FromResult<Commit?>(null),
			_now,
			TestContext.Current.CancellationToken).ConfigureAwait(false);

		await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
	}

	private static Func<CancellationToken, Task<T?>> Throws<T>() where T : class
		=> _ => throw new InvalidOperationException("Codacy unreachable");

	private static Commit Commit(string sha)
		=> new()
		{
			Sha = sha,
			Id = 1,
			CommitTimestamp = _now.AddHours(-4),
			AuthorName = "David Bond",
			AuthorEmail = "david.bond@panoramicdata.com",
			Message = "Something",
			StartedAnalysis = _now.AddHours(-4),
			EndedAnalysis = _now.AddHours(-4).AddMinutes(3)
		};
}
