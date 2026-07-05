namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// A single occurrence of an issue class (rule failure) in one repository.
/// </summary>
public sealed class IssueInstance
{
	/// <summary>The affected repository full name (e.g. "panoramicdata/Highlight.Api").</summary>
	public required string RepositoryFullName { get; init; }

	/// <summary>The failing rule result for this repository.</summary>
	public required RuleResult Result { get; init; }

	/// <summary>Whether this specific occurrence can be auto-remediated.</summary>
	public bool IsAutoRemediable { get; init; }
}

/// <summary>
/// An issue class (a single rule) aggregated across every repository it affects — the
/// "dimensional flip" of the repo-centric view.
/// </summary>
public sealed class IssueClassGroup
{
	/// <summary>The rule identifier (e.g. "CQ-05").</summary>
	public required string RuleId { get; init; }

	/// <summary>The human-readable rule name.</summary>
	public required string RuleName { get; init; }

	/// <summary>The category the rule belongs to.</summary>
	public required AssessmentCategory Category { get; init; }

	/// <summary>The highest severity observed across affected repositories.</summary>
	public required AssessmentSeverity Severity { get; init; }

	/// <summary>The affected repositories, one entry per repository.</summary>
	public required IReadOnlyList<IssueInstance> Instances { get; init; }

	/// <summary>Whether an automated remediation exists and can be applied to at least one repository.</summary>
	public bool HasAutomatedRemediation => Instances.Any(i => i.IsAutoRemediable);

	/// <summary>The number of affected repositories.</summary>
	public int AffectedRepositoryCount => Instances.Count;
}

/// <summary>
/// A category grouping of issue classes across the organization.
/// </summary>
public sealed class IssueCategoryGroup
{
	/// <summary>The assessment category.</summary>
	public required AssessmentCategory Category { get; init; }

	/// <summary>The issue classes (rules) that failed in at least one repository, most severe first.</summary>
	public required IReadOnlyList<IssueClassGroup> IssueClasses { get; init; }

	/// <summary>The highest severity across the category's issue classes.</summary>
	public AssessmentSeverity Severity => IssueClasses.Count == 0
		? AssessmentSeverity.Info
		: IssueClasses.Max(i => i.Severity);

	/// <summary>Whether any issue class in the category can be auto-remediated.</summary>
	public bool HasAutomatedRemediation => IssueClasses.Any(i => i.HasAutomatedRemediation);

	/// <summary>The distinct repositories affected by any issue class in this category.</summary>
	public int AffectedRepositoryCount => IssueClasses
		.SelectMany(i => i.Instances.Select(x => x.RepositoryFullName))
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.Count();
}

/// <summary>
/// The issue-centric ("dimensional flip") view of an organization assessment:
/// Category → Rule (issue class) → Repository.
/// </summary>
public sealed class IssueCentricView
{
	/// <summary>The categories that contain at least one failing issue class, most severe first.</summary>
	public required IReadOnlyList<IssueCategoryGroup> Categories { get; init; }

	/// <summary>All issue classes across all categories.</summary>
	public IEnumerable<IssueClassGroup> AllIssueClasses => Categories.SelectMany(c => c.IssueClasses);

	/// <summary>The total number of distinct failing issue classes.</summary>
	public int IssueClassCount => AllIssueClasses.Count();

	/// <summary>Whether any issue class anywhere can be auto-remediated.</summary>
	public bool HasAutomatedRemediation => Categories.Any(c => c.HasAutomatedRemediation);
}
