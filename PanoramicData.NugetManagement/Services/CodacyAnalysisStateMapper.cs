using Codacy.Api.Models;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Turns Codacy's two answers about analysis progress into one <see cref="CodacyAnalysisState"/>.
/// </summary>
/// <remarks>
/// Separated from the HTTP call so the decision can be tested without a Codacy account, and because
/// the decision is the part that is easy to get wrong: neither answer alone establishes that an
/// analysis is running.
/// </remarks>
internal static class CodacyAnalysisStateMapper
{
	/// <summary>
	/// Reads the analysis-progress overview and the last analysed commit together.
	/// </summary>
	/// <param name="overview">The analysis-progress answer, or null when it could not be obtained.</param>
	/// <param name="lastAnalysedCommit">
	/// The commit behind the current figures, or null when Codacy has analysed nothing.
	/// </param>
	/// <param name="now">The moment of asking, recorded so the answer's age is knowable.</param>
	/// <remarks>
	/// Two signals, because they cover different cases. <c>IsAnalyzing</c> is Codacy's own flag and
	/// answers for a repository being analysed for the first time; a re-analysis of an established
	/// repository shows up only as a commit whose analysis started and never ended. Reading either
	/// alone calls a running analysis finished.
	/// </remarks>
	public static CodacyAnalysisState From(
		FirstAnalysisOverview? overview,
		Commit? lastAnalysedCommit,
		DateTimeOffset now)
	{
		var startedButNotEnded = lastAnalysedCommit is
		{
			StartedAnalysis: not null,
			EndedAnalysis: null
		};

		return new CodacyAnalysisState
		{
			IsAnalysing = overview?.IsAnalyzing == true || startedButNotEnded,
			ProgressPercent = overview?.Progress,
			StartedAt = overview?.StartedAt ?? (startedButNotEnded ? lastAnalysedCommit!.StartedAnalysis : null),
			AnalysedSha = lastAnalysedCommit?.Sha,
			AnalysedAtUtc = lastAnalysedCommit?.EndedAnalysis,
			RetrievedAtUtc = now
		};
	}
}
