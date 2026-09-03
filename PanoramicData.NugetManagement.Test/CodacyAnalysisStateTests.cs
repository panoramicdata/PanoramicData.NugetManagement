using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="CodacyAnalysisStateMapper"/>, which reads Codacy's two answers about analysis
/// progress and decides whether an analysis is in flight.
/// </summary>
/// <remarks>
/// The reason any of this exists: a CQ-06 finding that reports grades Codacy is in the middle of
/// replacing reads as current fact, and the reader acts on a file that may already be fixed. The
/// derivation is separated from the HTTP call so the decision is testable without a Codacy account.
/// </remarks>
public class CodacyAnalysisStateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly DateTimeOffset _now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void IsAnalysing_WhenCodacyReportsAnAnalysisInProgress()
	{
		var state = CodacyAnalysisStateMapper.From(
			Progress(isAnalyzing: true, progress: 60, startedAt: _now.AddMinutes(-4)),
			lastAnalysedCommit: null,
			now: _now);

		state.IsAnalysing.Should().BeTrue();
		state.ProgressPercent.Should().Be(60);
		state.StartedAt.Should().Be(_now.AddMinutes(-4));
	}

	[Fact]
	public void IsAnalysing_WhenTheLastCommitStartedAnalysisAndHasNotEnded()
	{
		// The progress endpoint answers for a first analysis. A re-analysis of an established
		// repository shows up only as a commit whose analysis started and never ended, so reading the
		// flag alone would call a running analysis finished.
		var state = CodacyAnalysisStateMapper.From(
			Progress(isAnalyzing: false),
			Commit("abc1234", startedAnalysis: _now.AddMinutes(-2), endedAnalysis: null),
			_now);

		state.IsAnalysing.Should().BeTrue();
	}

	[Fact]
	public void IsNotAnalysing_WhenTheLastCommitFinishedItsAnalysis()
	{
		var state = CodacyAnalysisStateMapper.From(
			Progress(isAnalyzing: false),
			Commit("abc1234", startedAnalysis: _now.AddHours(-3), endedAnalysis: _now.AddHours(-3).AddMinutes(5)),
			_now);

		state.IsAnalysing.Should().BeFalse();
	}

	[Fact]
	public void CarriesTheAnalysedCommitAndWhenItFinished()
	{
		var ended = _now.AddHours(-3);

		var state = CodacyAnalysisStateMapper.From(
			Progress(isAnalyzing: false),
			Commit("abc1234", startedAnalysis: ended.AddMinutes(-5), endedAnalysis: ended),
			_now);

		state.AnalysedSha.Should().Be("abc1234");
		state.AnalysedAtUtc.Should().Be(ended);
	}

	[Fact]
	public void RecordsWhenWeAsked_SoTheAgeOfTheAnswerIsKnown()
	{
		// Without this the state is another undated claim, which is the whole defect being fixed.
		var state = CodacyAnalysisStateMapper.From(Progress(isAnalyzing: false), null, _now);

		state.RetrievedAtUtc.Should().Be(_now);
	}

	[Fact]
	public void IsNotAnalysing_WhenCodacyAnswersNothingAtAll()
	{
		var state = CodacyAnalysisStateMapper.From(overview: null, lastAnalysedCommit: null, now: _now);

		state.IsAnalysing.Should().BeFalse();
		state.AnalysedSha.Should().BeNull();
	}

	[Theory]
	[InlineData("abc1234", "abc1234", false)]
	[InlineData("9f8e7d6", "abc1234", true)]
	[InlineData("abc1234", null, false)]
	[InlineData(null, "abc1234", false)]
	[InlineData("", "abc1234", false)]
	public void IsBehind_OnlyWhenBothShasAreKnownAndDisagree(string? headSha, string? analysedSha, bool expected)
	{
		var state = new CodacyAnalysisState
		{
			AnalysedSha = analysedSha,
			RetrievedAtUtc = _now
		};

		state.IsBehind(headSha).Should().Be(expected);
	}

	[Fact]
	public void IsNotBehind_WhenTheShasAgreeOnlyInPrefix()
	{
		// Codacy returns full SHAs from one endpoint and git returns full SHAs from another, but a
		// short SHA anywhere in the chain must not read as a different commit.
		var state = new CodacyAnalysisState
		{
			AnalysedSha = "abc1234",
			RetrievedAtUtc = _now
		};

		state.IsBehind("abc1234567890").Should().BeFalse();
	}

	private static FirstAnalysisOverview Progress(
		bool isAnalyzing,
		int? progress = null,
		DateTimeOffset? startedAt = null)
		=> new()
		{
			IsFirstAnalysis = false,
			IsAnalyzing = isAnalyzing,
			Progress = progress,
			StartedAt = startedAt
		};

	private static Commit Commit(
		string sha,
		DateTimeOffset? startedAnalysis,
		DateTimeOffset? endedAnalysis)
		=> new()
		{
			Sha = sha,
			Id = 1,
			CommitTimestamp = _now.AddHours(-4),
			AuthorName = "David Bond",
			AuthorEmail = "david.bond@panoramicdata.com",
			Message = "Something",
			StartedAnalysis = startedAnalysis,
			EndedAnalysis = endedAnalysis
		};
}
