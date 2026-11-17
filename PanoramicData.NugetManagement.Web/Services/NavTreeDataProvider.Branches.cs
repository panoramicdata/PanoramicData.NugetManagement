using PanoramicData.Blazor;
using PanoramicData.Blazor.Models;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class NavTreeDataProvider
{
	/// <summary>
	/// Adds the Issues branch for one organisation: category → rule, the "dimensional flip" of the
	/// Repositories branch. It deliberately stops at rule level — a rule can affect every repository
	/// in the organisation, so listing repositories here would add thousands of sidebar nodes. The
	/// affected repositories and the bulk actions belong in the centre panel, which has room for
	/// per-repository checkboxes.
	/// </summary>
	/// <returns>
	/// The rolled-up health of the branch: the worst severity across its categories, so that the
	/// colour always has a visible cause beneath it. Grey only while nothing has been assessed at all,
	/// when the branch has no children either.
	/// </returns>
	private static PackageHealthStatus AddIssueHierarchy(
		List<NavItem> items,
		string organization,
		string issuesKey,
		string orgKey,
		List<RepositoryDashboardRow>? visibleRows)
	{
		// One entry per repository, which is what the rules were evaluated against. While a package
		// stood in for its repository, a repository publishing four of them counted as four affected
		// repositories against every rule it failed.
		// Augmented with the open Dependabot pull requests, exactly as DashboardService does when it
		// builds the same view for the centre panel: the tree's counts and the panel's contents have to
		// be the same answer, or one of them is lying.
		var assessed = visibleRows?
			.Where(r => r.Assessment is not null)
			.Select(r => new AssessedPackage(
				r.RepositoryFullName,
				DependabotIssueSynthesizer.Augment(r),
				r.RepositoryFullName))
			.ToList();

		// Once anything has been assessed the branch lists the whole catalogue, not just today's
		// failures: a rule nothing fails is still a rule the estate is held to, and it shows green.
		// Nothing assessed means no catalogue at all — see AddCleanCatalogue.
		var showsCatalogue = assessed is { Count: > 0 };
		var categories = showsCatalogue ? IssueCentricViewBuilder.Build(assessed!).Categories : [];

		var issuesStatus = NavHealthRollup.ForIssues(visibleRows, categories.Select(c => c.Severity));

		items.Add(new NavItem
		{
			Key = issuesKey,
			Text = "Issues",
			ParentKey = orgKey,
			IconCss = NavHealthRollup.Icon("fas fa-layer-group", issuesStatus),
			View = NavView.Issues,
			Organization = organization,
			IsLeaf = categories.Count == 0 && !showsCatalogue,
			SortOrder = 1,
			IssueCount = categories.Sum(c => c.IssueClasses.Sum(i => i.AffectedRepositoryCount)),
			HealthStatus = issuesStatus
		});

		for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
		{
			AddFailingCategory(items, organization, issuesKey, categories[categoryIndex], categoryIndex);
		}

		if (showsCatalogue)
		{
			AddCleanCatalogue(items, organization, issuesKey, categories);
		}

		return issuesStatus;
	}

	private static void AddFailingCategory(
		List<NavItem> items,
		string organization,
		string issuesKey,
		IssueCategoryGroup category,
		int categoryIndex)
	{
		var categoryKey = $"icat:{organization}:{category.Category}";

		items.Add(new NavItem
		{
			Key = categoryKey,
			Text = category.Category.ToString(),
			ParentKey = issuesKey,
			IconCss = GetIssueSeverityIcon(category.Severity),
			View = NavView.IssueCategoryDetail,
			Organization = organization,
			Category = category.Category,
			IsLeaf = category.IssueClasses.Count == 0,
			SortOrder = categoryIndex,
			IssueCount = category.IssueClasses.Count,
			AffectedRepoCount = category.AffectedRepositoryCount,
			HasErrors = category.Severity is AssessmentSeverity.Critical or AssessmentSeverity.Error,
			HasWarnings = category.Severity == AssessmentSeverity.Warning
		});

		for (var ruleIndex = 0; ruleIndex < category.IssueClasses.Count; ruleIndex++)
		{
			var issueClass = category.IssueClasses[ruleIndex];

			items.Add(new NavItem
			{
				Key = $"irule:{organization}:{category.Category}:{issueClass.RuleId}",
				Text = $"{issueClass.RuleId} {issueClass.RuleName}",
				ParentKey = categoryKey,
				IconCss = GetRuleIcon(issueClass.Severity),
				View = NavView.IssueRuleDetail,
				Organization = organization,
				Category = category.Category,
				RuleId = issueClass.RuleId,
				IsLeaf = true,
				SortOrder = ruleIndex,
				IssueCount = issueClass.AffectedRepositoryCount,
				AffectedRepoCount = issueClass.AffectedRepositoryCount,
				HasErrors = issueClass.Severity is AssessmentSeverity.Critical or AssessmentSeverity.Error,
				HasWarnings = issueClass.Severity == AssessmentSeverity.Warning
			});
		}
	}

	/// <summary>
	/// Adds the rest of the rule catalogue to the Issues branch: every category that owns a rule, and
	/// every rule, that no failure already put there. These are the green ones — nothing fails them.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Only called once something has been assessed. Painting the catalogue green over an organisation
	/// nobody has assessed would claim a clean bill of health that was never checked — the same
	/// distinction the repository's own Issues node makes between an empty inbox and an unread one.
	/// </para>
	/// <para>
	/// Failing entries keep the positions they were given above, and these sort after them, so what
	/// needs attention stays at the top of each list. A rule is matched by id alone: a synthesised
	/// Dependabot result carries a rule id the registry never had, and belongs in the branch on its
	/// own terms rather than being paired with a catalogue entry.
	/// </para>
	/// </remarks>
	private static void AddCleanCatalogue(
		List<NavItem> items,
		string organization,
		string issuesKey,
		IReadOnlyList<IssueCategoryGroup> failing)
	{
		var failingRuleIds = failing
			.SelectMany(category => category.IssueClasses.Select(issueClass => issueClass.RuleId))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var categoryOrder = failing.Count;

		var catalogue = RuleRegistry.Rules
			.GroupBy(rule => rule.Category)
			.OrderBy(group => group.Key.ToString(), StringComparer.OrdinalIgnoreCase);

		foreach (var group in catalogue)
		{
			var categoryKey = $"icat:{organization}:{group.Key}";
			var alreadyListed = failing.FirstOrDefault(category => category.Category == group.Key);

			if (alreadyListed is null)
			{
				items.Add(CleanCategoryNode(organization, issuesKey, categoryKey, group.Key, categoryOrder++));
			}

			// Clean rules follow whatever failing ones the category already contributed.
			var ruleOrder = alreadyListed?.IssueClasses.Count ?? 0;

			foreach (var rule in group.OrderBy(rule => rule.RuleId, StringComparer.OrdinalIgnoreCase).Where(rule => !failingRuleIds.Contains(rule.RuleId)))
			{
				items.Add(CleanRuleNode(organization, categoryKey, group.Key, rule, ruleOrder++));
			}
		}
	}

	private static NavItem CleanCategoryNode(string organization, string issuesKey, string categoryKey, AssessmentCategory category, int sortOrder)
		=> new()
		{
			Key = categoryKey,
			Text = category.ToString(),
			ParentKey = issuesKey,
			IconCss = "fas fa-layer-group text-success",
			View = NavView.IssueCategoryDetail,
			Organization = organization,
			Category = category,
			IsLeaf = false,
			SortOrder = sortOrder,
			IssueCount = 0,
			AffectedRepoCount = 0
		};

	private static NavItem CleanRuleNode(string organization, string categoryKey, AssessmentCategory category, IRule rule, int sortOrder)
		=> new()
		{
			Key = $"irule:{organization}:{category}:{rule.RuleId}",
			Text = $"{rule.RuleId} {rule.RuleName}",
			ParentKey = categoryKey,
			IconCss = "fas fa-check-circle text-success",
			View = NavView.IssueRuleDetail,
			Organization = organization,
			Category = category,
			RuleId = rule.RuleId,
			IsLeaf = true,
			SortOrder = sortOrder,
			IssueCount = 0,
			AffectedRepoCount = 0
		};

	/// <summary>
	/// Adds the branch accounting for packages whose declared repository is not ours to govern.
	/// </summary>
	/// <remarks>
	/// Absent when there is nothing to report, so the tree gains a node only where there is something
	/// to do about it. The children are leaves and carry no view: there is nothing to act on, and the
	/// reason on each names the repository declared, which is the nuspec that needs correcting.
	/// </remarks>
	private static void AddNotGovernedNodes(
		List<NavItem> items,
		string organization,
		string orgKey,
		IReadOnlyList<UngovernedPackage> packages)
	{
		if (packages.Count == 0)
		{
			return;
		}

		var notGovernedKey = NotGovernedKey(organization);

		items.Add(new NavItem
		{
			Key = notGovernedKey,
			Text = $"Not governed ({packages.Count})",
			ParentKey = orgKey,
			IconCss = "fas fa-circle-question",
			View = NavView.None,
			Organization = organization,
			IsLeaf = false,
			// Rank 3, after Repositories (0), Issues (1) and the org-level Work node (2).
			SortOrder = 3
		});

		foreach (var package in packages.OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase))
		{
			items.Add(new NavItem
			{
				Key = $"notgoverned:{organization}:{package.PackageId}",
				Text = $"{package.PackageId} — {package.Reason}",
				ParentKey = notGovernedKey,
				IconCss = "fas fa-circle-question",
				View = NavView.None,
				Organization = organization,
				PackageId = package.PackageId,
				IsLeaf = true
			});
		}
	}

	/// <summary>
	/// The packages of one organisation that belong to no repository we govern, honouring the name
	/// filter. They have no repository, so they are held beside the rows rather than among them.
	/// </summary>
	private List<UngovernedPackage> UngovernedPackagesFor(string organization, string fallbackOrganization)
	{
		var packages = _cache.GetUngovernedPackages()
			.Where(package => string.IsNullOrEmpty(package.Organization)
				? string.Equals(organization, fallbackOrganization, StringComparison.OrdinalIgnoreCase)
				: string.Equals(package.Organization, organization, StringComparison.OrdinalIgnoreCase));

		if (FilterRegex is not null)
		{
			packages = packages.Where(package => FilterRegex.IsMatch(package.PackageId));
		}

		return [.. packages];
	}
}
