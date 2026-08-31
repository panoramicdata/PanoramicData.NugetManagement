namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// The closed catalogue of work the application can queue.
/// </summary>
/// <remarks>
/// Closed deliberately. A queue of arbitrary delegates cannot be written to disk and picked up
/// again after a restart; a queue of named work can. Adding a member here without adding it to
/// <see cref="Services.WorkExecutors"/> fails a test rather than failing at run time.
/// </remarks>
public enum WorkKind
{
	/// <summary>Clone one repository locally.</summary>
	Clone,

	/// <summary>Re-assess one repository against every rule.</summary>
	Reassess,

	/// <summary>Apply every available auto-remediation to one repository.</summary>
	FixAll,

	/// <summary>Apply the auto-remediations of one assessment category. Parameter: <c>category</c>.</summary>
	FixCategory,

	/// <summary>Apply the auto-remediation of one rule. Parameter: <c>ruleId</c>.</summary>
	FixRule,

	/// <summary>Build one repository.</summary>
	Build,

	/// <summary>Run one repository's tests.</summary>
	Test,

	/// <summary>
	/// Classify one repository's open Dependabot pull requests, close the ones it has outgrown, and
	/// raise an issue for each dependency nothing here can fix.
	/// </summary>
	/// <remarks>
	/// Its own kind rather than a tail on <see cref="FixAll"/>: it is the only work that mutates
	/// GitHub, so it is worth being separately queueable, separately cancellable, and its own row in
	/// the queue. Lanes run in order, so queueing it after <see cref="Reassess"/> on the same lane is
	/// all the sequencing it needs.
	/// </remarks>
	TriageDependabot,

	/// <summary>Pull and push one repository.</summary>
	GitSync,

	/// <summary>Commit and push one repository's working tree.</summary>
	CommitAndPush,

	/// <summary>Publish one repository's packages.</summary>
	Publish,

	/// <summary>
	/// Read one organisation's package list from NuGet, then fan out re-assessment across the
	/// repositories it names. Organisation-scoped: there is no one repository it belongs to.
	/// </summary>
	RediscoverOrganization,

	/// <summary>Work out which repositories an organisation-wide re-assessment covers, then fan out.</summary>
	DiscoverReassessTargets,

	/// <summary>Work out which repositories are available to clone, then fan out.</summary>
	DiscoverCloneTargets,

	/// <summary>Rediscover and re-assess every organisation.</summary>
	RefreshAll
}
