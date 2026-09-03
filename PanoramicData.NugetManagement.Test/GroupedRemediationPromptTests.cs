using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the top-level AI remediation prompt, which groups failures under category
/// headings so the AI sees the full picture of everything the repository needs fixed.
/// </summary>
public class GroupedRemediationPromptTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RuleResult Failure(
		string ruleId,
		AssessmentCategory category,
		AssessmentSeverity severity) => new()
		{
			RuleId = ruleId,
			RuleName = $"{ruleId} name",
			Category = category,
			Severity = severity,
			Passed = false,
			Message = $"{ruleId} message",
			Advisory = new RuleAdvisory
			{
				Summary = $"Fix {ruleId}",
				Detail = $"Detailed guidance for {ruleId}."
			}
		};

	private static RepositoryDashboardRow Row(params RuleResult[] results) => new()
	{
		RepositoryFullName = "panoramicdata/Athonet.Api",
		Packages = [new() { PackageId = "Athonet.Api" }],
		LocalPath = @"C:\repos\Athonet.Api",
		Assessment = new RepoAssessment
		{
			RepositoryFullName = "panoramicdata/Athonet.Api",
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = [.. results]
		}
	};

	[Fact]
	public void MultipleCategories_EmitsCategoryHeadingsWithIssueCounts()
	{
		var row = Row(
			Failure("CI-09", AssessmentCategory.CiCd, AssessmentSeverity.Error),
			Failure("CI-05", AssessmentCategory.CiCd, AssessmentSeverity.Error),
			Failure("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning));

		var prompt = DashboardService.GenerateRemediationPrompt(row);
		Output.WriteLine(prompt);

		prompt.Should().Contain("## CiCd (2 issues)");
		prompt.Should().Contain("## CodeQuality (1 issue)");
	}

	[Fact]
	public void MultipleCategories_DemotesRuleHeadingsToLevelThree()
	{
		var row = Row(
			Failure("CI-09", AssessmentCategory.CiCd, AssessmentSeverity.Error),
			Failure("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning));

		var lines = DashboardService.GenerateRemediationPrompt(row).Split('\n');

		lines.Should().Contain("### [CI-09] CI-09 name");
		lines.Should().NotContain(l => l.StartsWith("## [", StringComparison.Ordinal));
	}

	[Fact]
	public void MultipleCategories_OrdersCategoriesByWorstSeverityFirst()
	{
		var row = Row(
			Failure("VER-03", AssessmentCategory.Versioning, AssessmentSeverity.Info),
			Failure("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning),
			Failure("CI-09", AssessmentCategory.CiCd, AssessmentSeverity.Error));

		var prompt = DashboardService.GenerateRemediationPrompt(row);

		var cicd = prompt.IndexOf("## CiCd", StringComparison.Ordinal);
		var codeQuality = prompt.IndexOf("## CodeQuality", StringComparison.Ordinal);
		var versioning = prompt.IndexOf("## Versioning", StringComparison.Ordinal);

		cicd.Should().BeLessThan(codeQuality);
		codeQuality.Should().BeLessThan(versioning);
	}

	[Fact]
	public void MultipleCategories_OrdersRulesWithinACategoryBySeverityThenRuleId()
	{
		var row = Row(
			Failure("CI-05", AssessmentCategory.CiCd, AssessmentSeverity.Warning),
			Failure("CI-09", AssessmentCategory.CiCd, AssessmentSeverity.Error),
			Failure("CI-01", AssessmentCategory.CiCd, AssessmentSeverity.Error),
			Failure("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning));

		var prompt = DashboardService.GenerateRemediationPrompt(row);

		var ci01 = prompt.IndexOf("[CI-01]", StringComparison.Ordinal);
		var ci09 = prompt.IndexOf("[CI-09]", StringComparison.Ordinal);
		var ci05 = prompt.IndexOf("[CI-05]", StringComparison.Ordinal);

		ci01.Should().BeLessThan(ci09);
		ci09.Should().BeLessThan(ci05);
	}

	[Fact]
	public void SingleCategory_KeepsFlatRuleHeadingsWithNoCategorySection()
	{
		var row = Row(
			Failure("CI-09", AssessmentCategory.CiCd, AssessmentSeverity.Error),
			Failure("CI-05", AssessmentCategory.CiCd, AssessmentSeverity.Warning));

		var prompt = DashboardService.GenerateRemediationPrompt(row);

		prompt.Should().NotContain("## CiCd");
		prompt.Should().Contain("## [CI-09] CI-09 name");
	}

	[Fact]
	public void CategoryPrompt_IsUnaffectedByGrouping()
	{
		var row = Row(
			Failure("CI-09", AssessmentCategory.CiCd, AssessmentSeverity.Error),
			Failure("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning));

		var prompt = DashboardService.GenerateCategoryRemediationPrompt(row, AssessmentCategory.CiCd);

		prompt.Should().NotContain("## CiCd");
		prompt.Should().Contain("## [CI-09] CI-09 name");
		prompt.Should().NotContain("CQ-05");
	}

	[Fact]
	public void ExcludingInfoFailures_DropsTheirCategoryEntirely()
	{
		var row = Row(
			Failure("CI-09", AssessmentCategory.CiCd, AssessmentSeverity.Error),
			Failure("CQ-05", AssessmentCategory.CodeQuality, AssessmentSeverity.Warning),
			Failure("VER-03", AssessmentCategory.Versioning, AssessmentSeverity.Info));

		var prompt = DashboardService.GenerateRemediationPrompt(row, includeInfo: false);

		prompt.Should().Contain("## CiCd (1 issue)");
		prompt.Should().NotContain("Versioning");
	}

	/// <summary>
	/// CQ-06's own fact shape: a list of per-file dictionaries. Joining it with string.Join put
	/// seven copies of "System.Collections.Generic.Dictionary`2[...]" in the prompt and threw away
	/// every grade the rule had gathered.
	/// </summary>
	private static RuleResult FileGradeFailure() => new()
	{
		RuleId = "CQ-06",
		RuleName = "Codacy file grades",
		Category = AssessmentCategory.CodeQuality,
		Severity = AssessmentSeverity.Info,
		Passed = false,
		Message = "2 file(s) graded below A.",
		Advisory = new RuleAdvisory
		{
			Summary = "Improve 2 file(s) graded below A.",
			Detail = "Factor out the repeated blocks.",
			Data = new Dictionary<string, object>
			{
				["files_below_minimum"] = 2,
				["worst_grade"] = "D",
				["files"] = new List<Dictionary<string, object?>>
				{
					new()
					{
						["path"] = "Codacy.Api.Test/Integration/PeopleApiTests.cs",
						["grade_letter"] = "D",
						["duplication_percent"] = 36
					},
					new()
					{
						["path"] = "Codacy.Api.Test/Integration/IssuesApiTests.cs",
						["grade_letter"] = "C",
						["duplication_percent"] = 59
					}
				}
			}
		}
	};

	[Fact]
	public void NestedFacts_AreExpandedRatherThanJoinedIntoTypeNames()
	{
		var prompt = DashboardService.GenerateRuleRemediationPrompt(
			Row(FileGradeFailure()),
			FileGradeFailure());
		Output.WriteLine(prompt);

		prompt.Should().NotContain("System.Collections",
			"a type name tells the model nothing about which files to fix");
		prompt.Should().Contain("Codacy.Api.Test/Integration/PeopleApiTests.cs");
		prompt.Should().Contain("Codacy.Api.Test/Integration/IssuesApiTests.cs");
		prompt.Should().Contain("duplication_percent",
			"the figure driving the grade is the one the fix turns on");
		prompt.Should().Contain("59");
	}

	[Fact]
	public void ScalarFacts_KeepTheirOneLineBulletForm()
	{
		var prompt = DashboardService.GenerateRuleRemediationPrompt(
			Row(FileGradeFailure()),
			FileGradeFailure());

		var lines = prompt.Split('\n');

		lines.Should().Contain("  - `files_below_minimum`: 2");
		lines.Should().Contain("  - `worst_grade`: D");
	}
}
