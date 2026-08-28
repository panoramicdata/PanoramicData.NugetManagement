using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that the issue view follows the assessments it is given. Applying the CommunityHealth fixes
/// left COM-01 and COM-02 still showing as failing, because nothing re-assessed the repositories once
/// their fixes had landed — everything downstream was reading the assessment taken before the change.
/// </summary>
public class IssueViewFreshnessTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void AnIssueClassShouldDisappear_WhenEveryAffectedRepositoryIsReassessedAsPassing()
	{
		var failing = Assessed("acme/Widget", com01Passed: false);
		var view = IssueCentricViewBuilder.Build([failing], _ => true);

		view.AllIssueClasses.Should().ContainSingle().Which.RuleId.Should().Be("COM-01");

		// What re-assessing after the fix produces.
		var fixedUp = Assessed("acme/Widget", com01Passed: true);
		var afterFix = IssueCentricViewBuilder.Build([fixedUp], _ => true);

		afterFix.AllIssueClasses.Should().BeEmpty("the fix has landed, so the rule no longer fails anywhere");
	}

	[Fact]
	public void AnIssueClassShouldRemain_ForRepositoriesTheRunDidNotReach()
	{
		var view = IssueCentricViewBuilder.Build(
			[Assessed("acme/Fixed", com01Passed: true), Assessed("acme/Untouched", com01Passed: false)],
			_ => true);

		var issueClass = view.AllIssueClasses.Should().ContainSingle().Subject;
		issueClass.Instances.Should().ContainSingle()
			.Which.RepositoryFullName.Should().Be("acme/Untouched");
	}

	private static AssessedPackage Assessed(string repositoryFullName, bool com01Passed)
		=> new(
			repositoryFullName,
			new RepoAssessment
			{
				RepositoryFullName = repositoryFullName,
				DefaultBranch = "main",
				AssessedAtUtc = DateTimeOffset.UtcNow,
				RuleResults =
				[
					new RuleResult
					{
						RuleId = "COM-01",
						RuleName = "SECURITY.md exists",
						Category = AssessmentCategory.CommunityHealth,
						Severity = AssessmentSeverity.Warning,
						Passed = com01Passed,
						Message = com01Passed ? "SECURITY.md found." : "SECURITY.md not found.",
						Advisory = com01Passed
							? null
							: new RuleAdvisory
							{
								Summary = "Add SECURITY.md",
								Detail = "Add a SECURITY.md to the repository root.",
								Data = new() { ["expected_path"] = "SECURITY.md", ["template_content"] = "policy" }
							}
					}
				]
			},
			repositoryFullName.Split('/')[^1]);
}
