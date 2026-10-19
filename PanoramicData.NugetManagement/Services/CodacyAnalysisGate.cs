using System.Text.Json;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Decides whether findings that come from Codacy are current enough to act on.
/// </summary>
/// <remarks>
/// Codacy's issues and grades describe the commit it last analysed, not the one checked out. A model handed a
/// finding that was already fixed has nothing to fix, so it finds something else to change: AlienFx.Api's CQ-05
/// session renamed a parameter back to the name a merged commit had just replaced, and opened a pull request
/// that would have undone the fix. The refusal costs a wait; acting on stale findings costs a wrong change.
/// <para>
/// Works from the rule's advisory data, where <see cref="CodacyFreshness.AddData"/> recorded the facts, and
/// accepts the values as <see cref="JsonElement"/> because assessments reloaded from the cache hold them that way.
/// </para>
/// </remarks>
public static class CodacyAnalysisGate
{
	/// <summary>
	/// Why findings should not be acted on yet, or null when they may be.
	/// </summary>
	/// <param name="data">The advisory data of a Codacy-graded rule; may be null.</param>
	/// <param name="headSha">The commit checked out in the clone, or null when it is not known.</param>
	/// <returns>
	/// A sentence for the user, or null. Null is also the answer when the data carries no freshness facts at all:
	/// an unknown freshness is not evidence of staleness, and refusing on it would stop every repository Codacy
	/// failed to describe.
	/// </returns>
	public static string? Refusal(IReadOnlyDictionary<string, object>? data, string? headSha)
	{
		if (data is null)
		{
			return null;
		}

		if (data.TryGetValue("codacy_is_analysing", out var analysing) && IsTrue(analysing))
		{
			return "Codacy is re-analysing this repository, so what it reports is being replaced. Wait for it to "
				+ "finish, then re-assess.";
		}

		if (data.TryGetValue("codacy_analysed_sha", out var value) && AsString(value) is { Length: > 0 } analysed)
		{
			var state = new CodacyAnalysisState { AnalysedSha = analysed, RetrievedAtUtc = DateTimeOffset.UtcNow };

			if (state.IsBehind(headSha))
			{
				return $"Codacy last analysed commit {Shorten(analysed)}, but this clone is at {Shorten(headSha!)}. "
					+ "What it reports may already have been fixed in between, and a change made now could undo that. "
					+ "Re-assess once Codacy has analysed the current commit.";
			}
		}

		return null;
	}

	private static bool IsTrue(object? value)
		=> value is true || value is JsonElement { ValueKind: JsonValueKind.True };

	private static string? AsString(object? value)
		=> value switch
		{
			string text => text,
			JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
			_ => null
		};

	private static string Shorten(string sha)
		=> sha.Trim() is { Length: > 7 } trimmed ? trimmed[..7] : sha.Trim();
}