using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Builds the issue-centric ("dimensional flip") view — Category → Rule → Repository — from a set
/// of per-repository assessments. This is the inverse of the repo-centric dashboard tree.
/// </summary>
public static class IssueCentricViewBuilder
{
	/// <summary>
	/// Builds the issue-centric view.
	/// </summary>
	/// <param name="assessments">Per-repository assessments (repository full name + result).</param>
	/// <param name="canRemediate">
	/// Optional predicate indicating whether a given rule result can be auto-remediated. When null,
	/// every occurrence is treated as non-remediable (manual/AI only). The Web layer supplies the
	/// remediation registry here.
	/// </param>
	public static IssueCentricView Build(
		IEnumerable<(string RepositoryFullName, RepoAssessment Assessment)> assessments,
		Func<RuleResult, bool>? canRemediate = null)
	{
		var failures = assessments
			.SelectMany(a => a.Assessment.RuleResults
				.Where(r => !r.Passed)
				.Select(r => (a.RepositoryFullName, Result: r)));

		var issueClasses = failures
			.GroupBy(f => f.Result.RuleId, StringComparer.OrdinalIgnoreCase)
			.Select(ruleGroup =>
			{
				var first = ruleGroup.First().Result;
				var instances = ruleGroup
					.Select(f => new IssueInstance
					{
						RepositoryFullName = f.RepositoryFullName,
						Result = f.Result,
						IsAutoRemediable = canRemediate?.Invoke(f.Result) == true
					})
					.OrderBy(i => i.RepositoryFullName, StringComparer.OrdinalIgnoreCase)
					.ToList();

				return new IssueClassGroup
				{
					RuleId = first.RuleId,
					RuleName = first.RuleName,
					Category = first.Category,
					Severity = instances.Max(i => i.Result.Severity),
					Instances = instances
				};
			})
			.ToList();

		var categories = issueClasses
			.GroupBy(i => i.Category)
			.Select(categoryGroup => new IssueCategoryGroup
			{
				Category = categoryGroup.Key,
				IssueClasses = [.. categoryGroup
					.OrderByDescending(i => i.Severity)
					.ThenByDescending(i => i.AffectedRepositoryCount)
					.ThenBy(i => i.RuleId, StringComparer.OrdinalIgnoreCase)]
			})
			.OrderByDescending(c => c.Severity)
			.ThenBy(c => c.Category.ToString(), StringComparer.OrdinalIgnoreCase)
			.ToList();

		return new IssueCentricView { Categories = categories };
	}
}
