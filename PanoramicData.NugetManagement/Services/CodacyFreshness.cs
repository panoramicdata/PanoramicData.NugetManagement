using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Says, in one sentence, how much a set of Codacy figures should be trusted.
/// </summary>
/// <remarks>
/// Shared between CQ-03 and CQ-06 so the two rules cannot come to disagree about the same repository
/// in the same panel. Silence is the common case and the deliberate one: forty-odd repositories are
/// current at any time, and a caveat on every one of them trains the reader to ignore the caveat
/// that matters.
/// </remarks>
internal static class CodacyFreshness
{
	/// <summary>
	/// The caveat these figures deserve, or null when they need none.
	/// </summary>
	/// <param name="state">
	/// What Codacy said about its own analysis, or null when that could not be established.
	/// </param>
	/// <param name="headSha">The checked-out commit, or null when it is unknown.</param>
	/// <returns>
	/// A sentence for the reader, or null when the figures describe the checked-out commit and nothing
	/// is running. Null is also the answer when <paramref name="state"/> is null: we cannot describe a
	/// freshness we failed to look up, and a made-up caveat is worse than none.
	/// </returns>
	public static string? Describe(CodacyAnalysisState? state, string? headSha)
	{
		if (state is null)
		{
			return null;
		}

		if (state.IsAnalysing)
		{
			var progress = state.ProgressPercent is { } percent ? $" — {percent}% complete" : string.Empty;
			var started = state.StartedAt is { } startedAt ? $", started {DescribeAge(startedAt)}" : string.Empty;
			var measured = state.AnalysedSha is { } sha
				? $" These figures are from commit {Shorten(sha)} and are being replaced."
				: " These figures are being replaced.";

			return $"Codacy is re-analysing this repository{progress}{started}.{measured}";
		}

		if (!state.IsBehind(headSha))
		{
			return null;
		}

		var when = state.AnalysedAtUtc is { } analysedAt ? $", {DescribeAge(analysedAt)}" : string.Empty;

		return $"Measured on commit {Shorten(state.AnalysedSha!)}{when}, not the commit checked out here.";
	}

	/// <summary>
	/// Which commit the current figures describe, whether or not that commit is stale.
	/// </summary>
	/// <returns>
	/// A sentence naming the analysed commit, or null when nothing has been analysed or the state
	/// could not be established.
	/// </returns>
	/// <remarks>
	/// For CQ-03, which asserts the integration is working and so is the right place to date that
	/// assertion. <see cref="Describe(CodacyAnalysisState?, string?)"/> stays silent when the figures
	/// are current, which is correct where the caveat would be noise but leaves CQ-03's claim undated.
	/// </remarks>
	public static string? DescribeLastAnalysis(CodacyAnalysisState? state)
	{
		if (state?.AnalysedSha is not { } sha)
		{
			return null;
		}

		var when = state.AnalysedAtUtc is { } analysedAt ? $", {DescribeAge(analysedAt)}" : string.Empty;

		return $"Last analysed commit {Shorten(sha)}{when}.";
	}

	/// <summary>
	/// A SHA at the length a reader can compare against `git log` output without scrolling.
	/// </summary>
	private static string Shorten(string sha)
	{
		var trimmed = sha.Trim();

		return trimmed.Length <= 7 ? trimmed : trimmed[..7];
	}

	/// <summary>
	/// A timestamp as an age, because "4 minutes ago" answers the question a reader is actually asking
	/// and an absolute UTC time makes them do the subtraction.
	/// </summary>
	private static string DescribeAge(DateTimeOffset moment)
	{
		var age = DateTimeOffset.UtcNow - moment;

		if (age < TimeSpan.Zero)
		{
			// Clock skew between us and Codacy. Saying "in 3 minutes" reads as a bug in the tool.
			return "just now";
		}

		if (age < TimeSpan.FromMinutes(1))
		{
			return "less than a minute ago";
		}

		if (age < TimeSpan.FromHours(1))
		{
			var minutes = (int)age.TotalMinutes;

			return $"{minutes} minute{(minutes == 1 ? "" : "s")} ago";
		}

		if (age < TimeSpan.FromDays(1))
		{
			var hours = (int)age.TotalHours;

			return $"{hours} hour{(hours == 1 ? "" : "s")} ago";
		}

		var days = (int)age.TotalDays;

		return $"{days} day{(days == 1 ? "" : "s")} ago";
	}
}
