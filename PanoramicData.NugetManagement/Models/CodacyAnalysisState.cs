namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// Where Codacy's analysis of a repository branch had got to when we asked.
/// </summary>
/// <remarks>
/// Codacy's grades and issues are measurements of one commit, and nothing in the response says which
/// one or when. Reported without this, a CQ-06 finding reads as current fact about the working tree,
/// so a file fixed ten minutes ago still shows as poor and a reader acts on work already done. This
/// carries the two facts that settle it: whether an analysis is in flight right now, and which commit
/// the figures actually describe.
/// </remarks>
public sealed class CodacyAnalysisState
{
	/// <summary>
	/// Whether Codacy is part-way through an analysis, so the figures alongside this are being
	/// replaced as they are read.
	/// </summary>
	public bool IsAnalysing { get; init; }

	/// <summary>How far through that analysis Codacy reports being, or null when it does not say.</summary>
	public int? ProgressPercent { get; init; }

	/// <summary>When the in-flight analysis started, or null when Codacy does not say.</summary>
	public DateTimeOffset? StartedAt { get; init; }

	/// <summary>
	/// The commit the current figures were measured on, or null when Codacy has analysed nothing.
	/// </summary>
	public string? AnalysedSha { get; init; }

	/// <summary>When that analysis finished, or null when it has not.</summary>
	public DateTimeOffset? AnalysedAtUtc { get; init; }

	/// <summary>
	/// When this answer was obtained. Every other property is a point-in-time claim, and the age of
	/// the claim is exactly what was missing before.
	/// </summary>
	public required DateTimeOffset RetrievedAtUtc { get; init; }

	/// <summary>
	/// Whether Codacy's figures describe an older commit than the one checked out.
	/// </summary>
	/// <param name="headSha">The branch head, or null when it is not known.</param>
	/// <remarks>
	/// Both SHAs have to be known for this to mean anything: an unknown head makes the question
	/// unanswerable, and answering "behind" would invent a staleness we cannot see. The comparison is
	/// by prefix in whichever direction is shorter, because the two endpoints need not agree on
	/// abbreviation and a short SHA must not read as a different commit.
	/// </remarks>
	public bool IsBehind(string? headSha)
	{
		if (string.IsNullOrWhiteSpace(headSha) || string.IsNullOrWhiteSpace(AnalysedSha))
		{
			return false;
		}

		var head = headSha.Trim();
		var analysed = AnalysedSha.Trim();
		var length = Math.Min(head.Length, analysed.Length);

		return !head.AsSpan(0, length).Equals(analysed.AsSpan(0, length), StringComparison.OrdinalIgnoreCase);
	}
}
