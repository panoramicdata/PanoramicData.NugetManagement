using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for which repositories a bulk apply targets. The count on the button and the repositories
/// the run visits come from here, together — when they were decided separately a category run
/// reported nine repositories and then synced and re-assessed eighty-three.
/// </summary>
public class BulkApplyScopeTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void ForRule_ShouldTakeOnlyRepositoriesThatCanBeFixed()
	{
		var rule = Rule("CPM-02",
			("acme/Fixable", true),
			("acme/NotFixable", false));

		var targets = BulkApplyScope.ForRule(rule, InScope("acme/Fixable", "acme/NotFixable"));

		targets.Should().ContainSingle().Which.Should().Be("acme/Fixable",
			"a repository the run cannot change should not be synced and re-assessed to discover that");
	}

	[Fact]
	public void ForRule_ShouldRespectScope()
	{
		var rule = Rule("CPM-02", ("acme/In", true), ("acme/Out", true));

		var targets = BulkApplyScope.ForRule(rule, InScope("acme/In"));

		targets.Should().ContainSingle().Which.Should().Be("acme/In");
	}

	[Fact]
	public void ForCategory_ShouldVisitARepositoryOnce_HoweverManyRulesItFails()
	{
		var category = Category(
			Rule("CPM-01", ("acme/Widget", true)),
			Rule("CPM-02", ("acme/Widget", true), ("acme/Gadget", true)));

		var targets = BulkApplyScope.ForCategory(category, InScope("acme/Widget", "acme/Gadget"));

		targets.Should().BeEquivalentTo(["acme/Gadget", "acme/Widget"]);
	}

	[Fact]
	public void ForCategory_ShouldExcludeRepositoriesWithOnlyManualIssues()
	{
		// The NuGetHygiene case: nearly every repository fails a package-update rule, and most of
		// those failures cannot be auto-applied.
		var category = Category(
			Rule("PKG-01", ("acme/Fixable", true)),
			Rule("PKG-05", ("acme/ManualOnly", false), ("acme/AlsoManual", false)));

		var targets = BulkApplyScope.ForCategory(category, InScope("acme/Fixable", "acme/ManualOnly", "acme/AlsoManual"));

		targets.Should().ContainSingle().Which.Should().Be("acme/Fixable");
	}

	[Fact]
	public void ForEverything_ShouldSpanCategoriesWithoutRepeatingARepository()
	{
		var view = new IssueCentricView
		{
			Categories =
			[
				Category(Rule("CPM-02", ("acme/Widget", true))),
				Category(Rule("LIC-02", ("acme/Widget", true), ("acme/Gadget", true)))
			]
		};

		var targets = BulkApplyScope.ForEverything(view, InScope("acme/Widget", "acme/Gadget"));

		targets.Should().BeEquivalentTo(["acme/Gadget", "acme/Widget"]);
	}

	[Fact]
	public void ForEverything_ShouldBeEmpty_WhenNothingCanBeFixed()
	{
		var view = new IssueCentricView
		{
			Categories = [Category(Rule("PKG-05", ("acme/Widget", false)))]
		};

		BulkApplyScope.ForEverything(view, InScope("acme/Widget")).Should().BeEmpty();
	}

	private static ISet<string> InScope(params string[] repositories)
		=> repositories.ToHashSet(StringComparer.OrdinalIgnoreCase);

	private static IssueClassGroup Rule(string ruleId, params (string Repository, bool AutoRemediable)[] instances)
		=> new()
		{
			RuleId = ruleId,
			RuleName = $"{ruleId} name",
			Category = AssessmentCategory.CentralPackageManagement,
			Severity = AssessmentSeverity.Error,
			Instances =
			[
				.. instances.Select(i => new IssueInstance
				{
					RepositoryFullName = i.Repository,
					PackageId = i.Repository.Split('/')[^1],
					IsAutoRemediable = i.AutoRemediable,
					Result = new RuleResult
					{
						RuleId = ruleId,
						RuleName = $"{ruleId} name",
						Category = AssessmentCategory.CentralPackageManagement,
						Severity = AssessmentSeverity.Error,
						Passed = false,
						Message = "failing"
					}
				})
			]
		};

	private static IssueCategoryGroup Category(params IssueClassGroup[] rules)
		=> new()
		{
			Category = AssessmentCategory.CentralPackageManagement,
			IssueClasses = [.. rules]
		};
}
