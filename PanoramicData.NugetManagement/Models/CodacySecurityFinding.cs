namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The severity Codacy assigns a security finding, and the whole basis of the SEC split:
/// SEC-01 owns <see cref="Critical"/>, SEC-02 owns <see cref="High"/>, SEC-03 owns the rest.
/// </summary>
/// <remarks>
/// Mirrors Codacy's own priority set rather than reusing <c>SrmPriority</c> from the client
/// library, so the models a rule reads do not change shape when that package is upgraded.
/// </remarks>
public enum CodacySecurityPriority
{
	/// <summary>Lowest severity.</summary>
	Low,

	/// <summary>Medium severity.</summary>
	Medium,

	/// <summary>High severity.</summary>
	High,

	/// <summary>Highest severity.</summary>
	Critical
}

/// <summary>
/// Where a finding stands against the organization's Codacy SLA, and the axis the advisory groups
/// by: <see cref="Overdue"/> first, then <see cref="DueSoon"/>, then <see cref="OnTrack"/>.
/// </summary>
/// <remarks>
/// Declared in urgency order, so ordering a group by this enum needs no lookup table. Only the
/// three open statuses appear: closed and ignored findings are excluded at the Codacy search, so a
/// finding that reaches this model is always outstanding.
/// </remarks>
public enum CodacySecuritySlaStatus
{
	/// <summary>Past its SLA due date.</summary>
	Overdue,

	/// <summary>Approaching its SLA due date.</summary>
	DueSoon,

	/// <summary>Open and within its SLA.</summary>
	OnTrack
}

/// <summary>
/// A single open security finding reported by Codacy for a repository.
/// </summary>
public sealed class CodacySecurityFinding
{
	/// <summary>Codacy's identifier for the finding.</summary>
	public required Guid Id { get; init; }

	/// <summary>The human-readable title, which is Codacy's own description of the problem.</summary>
	public required string Title { get; init; }

	/// <summary>The severity band the finding falls in.</summary>
	public required CodacySecurityPriority Priority { get; init; }

	/// <summary>
	/// Codacy's SLA status for the finding. Only open findings are ever fetched, so a closed or
	/// ignored status never appears here.
	/// </summary>
	public required CodacySecuritySlaStatus SlaStatus { get; init; }

	/// <summary>
	/// The security category, for example <c>CommandInjection</c>. Null for a finding Codacy has
	/// not categorised, which is a finding all the same.
	/// </summary>
	public string? SecurityCategory { get; init; }

	/// <summary>The scan that found it, for example <c>SAST</c>, <c>Secrets</c> or <c>IaC</c>.</summary>
	public string? ScanType { get; init; }

	/// <summary>A link to the finding in Codacy, so a reader can go straight to it.</summary>
	public string? HtmlUrl { get; init; }

	/// <summary>When the finding was opened.</summary>
	public DateTimeOffset OpenedAt { get; init; }

	/// <summary>When the finding falls due under the organization's Codacy SLA.</summary>
	public DateTimeOffset DueAt { get; init; }
}
