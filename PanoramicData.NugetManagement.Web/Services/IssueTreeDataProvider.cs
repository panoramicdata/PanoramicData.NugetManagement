using PanoramicData.Blazor;
using PanoramicData.Blazor.Models;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Provides <see cref="IssueNavItem"/> data for the issue-centric PDTree: Category → Rule →
/// Repository. This is the dimensional flip of the repo-centric navigation tree, so the same rule
/// can be fixed across every repository it affects.
/// </summary>
public class IssueTreeDataProvider : DataProviderBase<IssueNavItem>
{
	/// <summary>
	/// The issue-centric view to build the tree from. Null until the assessment data has loaded.
	/// </summary>
	public IssueCentricView? View { get; set; }

	/// <inheritdoc />
	public override Task<DataResponse<IssueNavItem>> GetDataAsync(DataRequest<IssueNavItem> request, CancellationToken cancellationToken)
	{
		var items = BuildItems();
		return Task.FromResult(new DataResponse<IssueNavItem>(items, items.Count));
	}

	/// <summary>
	/// Flattens the view into parent-before-child order, which is what PDTree requires: it resolves
	/// each item's parent as it goes and throws if a child is seen first.
	/// </summary>
	public List<IssueNavItem> BuildItems()
	{
		var items = new List<IssueNavItem>();
		if (View is null)
		{
			return items;
		}

		foreach (var category in View.Categories)
		{
			var categoryKey = $"cat:{category.Category}";
			items.Add(new IssueNavItem
			{
				Key = categoryKey,
				Text = category.Category.ToString(),
				Kind = IssueNodeKind.Category,
				IsLeaf = category.IssueClasses.Count == 0,
				Category = category
			});

			foreach (var rule in category.IssueClasses)
			{
				// Scoped by category as well as rule id: a rule belongs to exactly one category, but
				// keying on both keeps the key meaningful on its own.
				var ruleKey = $"rule:{category.Category}:{rule.RuleId}";
				items.Add(new IssueNavItem
				{
					Key = ruleKey,
					ParentKey = categoryKey,
					Text = $"{rule.RuleId} {rule.RuleName}",
					Kind = IssueNodeKind.Rule,
					IsLeaf = rule.Instances.Count == 0,
					Rule = rule,
					RuleId = rule.RuleId
				});

				// The index disambiguates: a repository can host several packages, so it legitimately
				// appears more than once under the same rule — one occurrence per package. Keys must
				// still be unique, or PDTree rejects the whole tree.
				var occurrence = 0;
				foreach (var instance in rule.Instances)
				{
					// The same repository appears under every rule it fails, so the rule must be part
					// of the key or those nodes would collide.
					items.Add(new IssueNavItem
					{
						Key = $"repo:{rule.RuleId}:{instance.RepositoryFullName}:{occurrence++}",
						ParentKey = ruleKey,
						Text = instance.RepositoryFullName,
						Kind = IssueNodeKind.Repository,
						IsLeaf = true,
						Instance = instance,
						RuleId = rule.RuleId
					});
				}
			}
		}

		return items;
	}
}
