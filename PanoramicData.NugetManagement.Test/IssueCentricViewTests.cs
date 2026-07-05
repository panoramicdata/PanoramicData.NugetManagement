using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the issue-centric ("dimensional flip") view builder and combined prompt builder.
/// </summary>
public class IssueCentricViewTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RuleResult Result(
		string ruleId,
		AssessmentCategory category,
		AssessmentSeverity severity,
		bool passed,
		string? detail = null) => new()
		{
			RuleId = ruleId,
			RuleName = $"{ruleId} name",
			Category = category,
			Severity = severity,
			Passed = passed,
			Message = $"{ruleId} message",
			Advisory = passed ? null : new RuleAdvisory
			{
				Summary = $"Fix {ruleId}",
				Detail = detail ?? $"Detailed guidance for {ruleId}."
			}
		};

	private static (string, RepoAssessment) Repo(string fullName, params RuleResult[] results)
		=> (fullName, new RepoAssessment
		{
			RepositoryFullName = fullName,
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = [.. results]
		});

	[Fact]
	public void Build_GroupsRuleAcrossRepos_AndExcludesPasses()
	{
		var entries = new[]
		{
			Repo("panoramicdata/RepoA",
				Result("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning, passed: false),
				Result("CI-01", AssessmentCategory.CiCd, AssessmentSeverity.Error, passed: true)),
			Repo("panoramicdata/RepoB",
				Result("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Error, passed: false))
		};

		var view = IssueCentricViewBuilder.Build(entries);

		var codeQuality = view.Categories.Should().ContainSingle(c => c.Category == AssessmentCategory.CodeQuality).Subject;
		var cq05 = codeQuality.IssueClasses.Should().ContainSingle(i => i.RuleId == "CQ-05").Subject;
		cq05.AffectedRepositoryCount.Should().Be(2);
		cq05.Instances.Select(i => i.RepositoryFullName)
			.Should().BeEquivalentTo(["panoramicdata/RepoA", "panoramicdata/RepoB"]);
		// Highest severity across repos wins.
		cq05.Severity.Should().Be(AssessmentSeverity.Error);
		// Passed rule (CI-01) must not appear.
		view.AllIssueClasses.Should().NotContain(i => i.RuleId == "CI-01");
	}

	[Fact]
	public void Build_MarksAutoRemediable_FromPredicate()
	{
		var entries = new[]
		{
			Repo("panoramicdata/RepoA", Result("LIC-01", AssessmentCategory.Licensing, AssessmentSeverity.Error, passed: false)),
			Repo("panoramicdata/RepoB", Result("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning, passed: false))
		};

		// Only LIC-01 is auto-remediable.
		var view = IssueCentricViewBuilder.Build(entries, r => r.RuleId == "LIC-01");

		view.AllIssueClasses.Single(i => i.RuleId == "LIC-01").HasAutomatedRemediation.Should().BeTrue();
		view.AllIssueClasses.Single(i => i.RuleId == "CQ-05").HasAutomatedRemediation.Should().BeFalse();
	}

	[Fact]
	public void Build_OrdersCategoriesBySeverityDescending()
	{
		var entries = new[]
		{
			Repo("panoramicdata/RepoA",
				Result("CQ-01", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning, passed: false),
				Result("LIC-01", AssessmentCategory.Licensing, AssessmentSeverity.Critical, passed: false))
		};

		var view = IssueCentricViewBuilder.Build(entries);

		view.Categories[0].Category.Should().Be(AssessmentCategory.Licensing, "Critical outranks Warning");
	}

	[Fact]
	public void CombinedPrompt_ForRule_IncludesEveryRepoAndDetail()
	{
		var view = IssueCentricViewBuilder.Build(
		[
			Repo("panoramicdata/RepoA", Result("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning, passed: false, detail: "AAA detail")),
			Repo("panoramicdata/RepoB", Result("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning, passed: false, detail: "BBB detail"))
		]);

		var prompt = CombinedRemediationPromptBuilder.ForRule(view.AllIssueClasses.Single(i => i.RuleId == "CQ-05"));

		prompt.Should().Contain("panoramicdata/RepoA").And.Contain("panoramicdata/RepoB");
		prompt.Should().Contain("AAA detail").And.Contain("BBB detail");
		prompt.Should().Contain("2 repositories");
	}

	[Fact]
	public void CombinedPrompt_ForCategory_CanExcludeAutoRemediable()
	{
		var view = IssueCentricViewBuilder.Build(
			[
				Repo("panoramicdata/RepoA",
					Result("LIC-01", AssessmentCategory.Licensing, AssessmentSeverity.Error, passed: false, detail: "license detail"))
			],
			canRemediate: r => r.RuleId == "LIC-01");

		var category = view.Categories.Single(c => c.Category == AssessmentCategory.Licensing);
		var prompt = CombinedRemediationPromptBuilder.ForCategory(category, onlyNonRemediable: true);

		prompt.Should().NotContain("license detail", "auto-remediable classes are excluded from the manual prompt");
	}
}
