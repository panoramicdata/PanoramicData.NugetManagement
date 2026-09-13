namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The open Codacy security findings for a single repository.
/// </summary>
public sealed class CodacySecurityReport
{
	/// <summary>
	/// Whether Codacy holds the repository.
	/// </summary>
	/// <remarks>
	/// The organization-scoped security search cannot answer this on its own: it returns 200 with
	/// an empty list both for a repository with no findings and for a name Codacy has never heard
	/// of. Reporting the second as "no security findings" is the trap CQ-05 documents — a silent
	/// pass for a repository nobody is analysing — so an empty result is corroborated against the
	/// repository endpoint, which 404s only for a repository that was never added.
	/// </remarks>
	public required bool IsTracked { get; init; }

	/// <summary>
	/// Every open finding, across all four priorities. Each SEC rule filters this to its own band,
	/// so the three rules share one fetch.
	/// </summary>
	public IReadOnlyList<CodacySecurityFinding> Findings { get; init; } = [];
}
