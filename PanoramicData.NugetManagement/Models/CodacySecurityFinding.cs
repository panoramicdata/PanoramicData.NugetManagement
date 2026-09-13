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
	/// Codacy's SLA status for the finding — <c>OnTrack</c>, <c>DueSoon</c> or <c>Overdue</c>.
	/// Only open findings are ever fetched, so a closed or ignored status never appears here.
	/// </summary>
	public required string Status { get; init; }

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
