using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="EstateTableModel"/>: which columns the estate table has, what each cell says,
/// and what order and filter do to the rows.
/// </summary>
public class EstateTableModelTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static RepositoryDashboardRow Row(
		string name,
		bool assessed = true,
		bool cloned = true,
		bool governed = true,
		params (AssessmentCategory Category, int Critical, int Error, int Warning, int Info)[] findings)
	{
		// Rule results and category summaries are built from the same findings, as they are in real data:
		// the total issue count comes from the results and the cells from the summaries, and a test row
		// that set only one would pass or fail for a reason that has nothing to do with the model.
		var results = new List<RuleResult>();

		foreach (var finding in findings)
		{
			AddFailures(results, finding.Category, AssessmentSeverity.Critical, finding.Critical);
			AddFailures(results, finding.Category, AssessmentSeverity.Error, finding.Error);
			AddFailures(results, finding.Category, AssessmentSeverity.Warning, finding.Warning);
			AddFailures(results, finding.Category, AssessmentSeverity.Info, finding.Info);
		}

		var row = new RepositoryDashboardRow
		{
			Organization = "panoramicdata",
			RepositoryFullName = $"panoramicdata/{name}",
			IsClonedLocally = cloned,
			IsGoverned = governed,
			Assessment = assessed
				? new RepoAssessment
				{
					RepositoryFullName = $"panoramicdata/{name}",
					DefaultBranch = "main",
					AssessedAtUtc = DateTimeOffset.UtcNow,
					RuleResults = results
				}
				: null
		};

		foreach (var finding in findings)
		{
			row.CategorySummaries[finding.Category] = new CategorySummary
			{
				Criticals = finding.Critical,
				Errors = finding.Error,
				Warnings = finding.Warning,
				Infos = finding.Info
			};
		}

		return row;
	}

	private static void AddFailures(
		List<RuleResult> results,
		AssessmentCategory category,
		AssessmentSeverity severity,
		int count)
	{
		for (var i = 0; i < count; i++)
		{
			results.Add(new RuleResult
			{
				RuleId = $"{category}-{severity}-{i}",
				RuleName = "A failing rule",
				Category = category,
				Severity = severity,
				Passed = false,
				Message = "wrong"
			});
		}
	}

	private static EstateTableModel Build(
		IEnumerable<RepositoryDashboardRow> rows,
		string? filter = null,
		EstateSortColumn sort = EstateSortColumn.Default,
		bool descending = false,
		AssessmentCategory? category = null,
		params string[] excluded)
		=> EstateTableModel.Build(
			rows,
			name => excluded.Contains(name, StringComparer.OrdinalIgnoreCase),
			filter,
			sort,
			descending,
			category);

	[Fact]
	public void Categories_AreOnlyThoseWithAFailure_InEnumOrder()
	{
		var model = Build(
		[
			Row("A", findings: [(AssessmentCategory.CodeQuality, 0, 1, 0, 0)]),
			Row("B", findings: [(AssessmentCategory.CiCd, 0, 0, 2, 0), (AssessmentCategory.Versioning, 0, 0, 0, 0)])
		]);

		model.Categories.Should().Equal(
			[AssessmentCategory.CiCd, AssessmentCategory.CodeQuality],
			"Versioning has no failing rule anywhere so takes no space, and the order is the enum's so the layout is stable");
	}

	[Theory]
	[InlineData(1, 0, 0, 0, 1, EstateSeverity.Error)]
	[InlineData(0, 2, 0, 0, 2, EstateSeverity.Error)]
	[InlineData(0, 0, 3, 0, 3, EstateSeverity.Warning)]
	[InlineData(0, 0, 0, 4, 4, EstateSeverity.Info)]
	[InlineData(1, 1, 1, 1, 4, EstateSeverity.Error)]
	[InlineData(0, 0, 0, 0, 0, EstateSeverity.Clean)]
	public void ACell_CountsTheFailuresAndShowsTheWorstSeverity(
		int critical, int error, int warning, int info, int count, EstateSeverity severity)
	{
		var model = Build(
		[
			Row("A", findings: [(AssessmentCategory.CiCd, critical, error, warning, info)]),
			Row("B", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)])
		]);

		var cell = model.Rows.Single(r => r.Name == "A").Cells[AssessmentCategory.CiCd];

		cell.Count.Should().Be(count);
		cell.Severity.Should().Be(severity);
	}

	[Fact]
	public void AWaivedRule_IsNotAFailure()
	{
		var row = Row("A", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]);
		row.CategorySummaries[AssessmentCategory.CiCd].Waived = 5;

		Build([row]).Rows[0].Cells[AssessmentCategory.CiCd].Count.Should().Be(1,
			"a waived rule is one the estate has excused, not one the repository fails");
	}

	[Fact]
	public void AnUnassessedRepository_ShowsUnknown_NotClean()
	{
		var model = Build(
		[
			Row("Assessed", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("Never", assessed: false)
		]);

		model.Rows.Single(r => r.Name == "Never").Cells[AssessmentCategory.CiCd].Severity
			.Should().Be(EstateSeverity.Unknown,
				"nothing has been checked, and a blank that looks like a clean bill of health is the worst answer");
	}

	[Fact]
	public void AnAssessedRepositoryWithNoFindingInACategory_IsClean()
	{
		var model = Build(
		[
			Row("A", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("B")
		]);

		model.Rows.Single(r => r.Name == "B").Cells[AssessmentCategory.CiCd].Severity
			.Should().Be(EstateSeverity.Clean);
	}

	[Fact]
	public void UnknownGitBuildAndTestState_IsUnknown_NotGood()
	{
		var row = Build([Row("A")]).Rows[0];

		row.Uncommitted.Should().Be(EstateHealth.Unknown);
		row.Unpushed.Should().Be(EstateHealth.Unknown);
		row.Sync.Should().Be(EstateHealth.Unknown);
		row.Build.Should().Be(EstateHealth.Unknown);
		row.Test.Should().Be(EstateHealth.Unknown);
	}

	[Fact]
	public void TheGitFlags_MapToGoodAndBad()
	{
		var good = Row("Good");
		good.IsWorkingTreeClean = true;
		good.HasUnpushedCommits = false;
		good.IsSyncedWithOrigin = true;
		good.CurrentBranch = "main";

		var bad = Row("Bad");
		bad.IsWorkingTreeClean = false;
		bad.HasUnpushedCommits = true;
		bad.IsSyncedWithOrigin = false;

		var model = Build([good, bad]);

		var goodRow = model.Rows.Single(r => r.Name == "Good");
		goodRow.Uncommitted.Should().Be(EstateHealth.Good);
		goodRow.Unpushed.Should().Be(EstateHealth.Good);
		goodRow.Sync.Should().Be(EstateHealth.Good);
		goodRow.Branch.Should().Be("main");

		var badRow = model.Rows.Single(r => r.Name == "Bad");
		badRow.Uncommitted.Should().Be(EstateHealth.Bad);
		badRow.Unpushed.Should().Be(EstateHealth.Bad);
		badRow.Sync.Should().Be(EstateHealth.Bad);
	}

	[Fact]
	public void BuildAndTestResults_MapToGoodAndBad()
	{
		var passes = Row("Passes");
		passes.LastBuildState = RepositoryBuildState.Succeeded;
		passes.RememberTestResult(RepositoryTestState.Passed, DateTimeOffset.UtcNow);

		var fails = Row("Fails");
		fails.LastBuildState = RepositoryBuildState.Failed;
		fails.RememberTestResult(RepositoryTestState.Failed, DateTimeOffset.UtcNow);

		var model = Build([passes, fails]);

		model.Rows.Single(r => r.Name == "Passes").Build.Should().Be(EstateHealth.Good);
		model.Rows.Single(r => r.Name == "Passes").Test.Should().Be(EstateHealth.Good);
		model.Rows.Single(r => r.Name == "Fails").Build.Should().Be(EstateHealth.Bad);
		model.Rows.Single(r => r.Name == "Fails").Test.Should().Be(EstateHealth.Bad);
	}

	[Fact]
	public void ARepositoryThatCannotBeActedOn_IsShownButInert_AndSaysWhy()
	{
		var model = Build(
		[
			Row("Fine"),
			Row("NoClone", cloned: false),
			Row("Dropped"),
			Row("Foreign", governed: false)
		],
		excluded: ["panoramicdata/Dropped"]);

		model.Rows.Should().HaveCount(4, "hiding them would make the estate look smaller than it is");
		model.Rows.Single(r => r.Name == "Fine").CanSelect.Should().BeTrue();

		model.Rows.Single(r => r.Name == "NoClone").InertReason.Should().Be("not cloned");
		model.Rows.Single(r => r.Name == "Dropped").InertReason.Should().Be("excluded");
		model.Rows.Single(r => r.Name == "Foreign").InertReason.Should().Be("not governed");
		model.Rows.Where(r => !r.CanSelect).Should().HaveCount(3);
	}

	[Fact]
	public void TheDefaultOrder_PutsWhatIsWrongFirst()
	{
		var broken = Row("Broken");
		broken.LastBuildState = RepositoryBuildState.Failed;

		var untested = Row("Untested");

		var green = Row("Green");
		green.LastBuildState = RepositoryBuildState.Succeeded;
		green.RememberTestResult(RepositoryTestState.Passed, DateTimeOffset.UtcNow);

		var failingTests = Row("FailingTests");
		failingTests.LastBuildState = RepositoryBuildState.Succeeded;
		failingTests.RememberTestResult(RepositoryTestState.Failed, DateTimeOffset.UtcNow);

		var model = Build([green, untested, failingTests, broken]);

		model.Rows.Select(r => r.Name).Should().Equal(
			["Broken", "FailingTests", "Untested", "Green"],
			"failed builds, then failed tests, then anything not known, then what both builds and passes");
	}

	[Fact]
	public void WithinTheSameState_MoreIssuesComeFirst_ThenByName()
	{
		var model = Build(
		[
			Row("B", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("A", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("C", findings: [(AssessmentCategory.CiCd, 0, 3, 0, 0)])
		]);

		model.Rows.Select(r => r.Name).Should().Equal(["C", "A", "B"]);
	}

	[Fact]
	public void Filter_MatchesTheNameWithoutRegardToCase_AndKeepsTheColumns()
	{
		var model = Build(
		[
			Row("Cisco.Api", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("Toggl.Api", findings: [(AssessmentCategory.CodeQuality, 0, 1, 0, 0)])
		],
		filter: "CISCO");

		model.Rows.Should().ContainSingle().Which.Name.Should().Be("Cisco.Api");
		model.TotalCount.Should().Be(2, "the total is what the selection line compares against");
		model.Categories.Should().HaveCount(2, "the layout must not shift as the filter is typed");
	}

	[Fact]
	public void SortByACategory_PutsTheMostFailuresFirst_AndDescendingReversesIt()
	{
		var rows = new[]
		{
			Row("Few", findings: [(AssessmentCategory.CiCd, 0, 1, 0, 0)]),
			Row("Many", findings: [(AssessmentCategory.CiCd, 0, 5, 0, 0)]),
			Row("None", findings: [(AssessmentCategory.CiCd, 0, 0, 0, 0)])
		};

		Build(rows, sort: EstateSortColumn.Category, category: AssessmentCategory.CiCd)
			.Rows.Select(r => r.Name).Should().Equal(["Many", "Few", "None"]);

		Build(rows, sort: EstateSortColumn.Category, descending: true, category: AssessmentCategory.CiCd)
			.Rows.Select(r => r.Name).Should().Equal(["None", "Few", "Many"]);
	}

	[Fact]
	public void SelectableNames_AreTheVisibleRowsThatCanBeActedOn()
	{
		var model = Build(
		[
			Row("Alpha.Api"),
			Row("Alpha.Cli", cloned: false),
			Row("Beta.Api")
		],
		filter: "alpha");

		model.SelectableNames.Should().Equal(["panoramicdata/Alpha.Api"],
			"a header checkbox ticks what is on screen and can be acted on, nothing else");
	}

	[Fact]
	public void HiddenSelectedCount_CountsTickedRowsTheFilterHides()
	{
		var model = Build([Row("Alpha.Api"), Row("Beta.Api"), Row("Gamma.Api")], filter: "alpha");

		var selection = new RepositorySelection();
		selection.SelectAll(["panoramicdata/Alpha.Api", "panoramicdata/Beta.Api", "panoramicdata/Gamma.Api"]);

		model.HiddenSelectedCount(selection).Should().Be(2,
			"a press acts on every ticked row, so the two the filter hides must still be counted");
	}

	[Fact]
	public void ZeroRepositories_ProducesAnEmptyModel_WithoutThrowing()
	{
		var model = Build([]);

		model.Rows.Should().BeEmpty();
		model.Categories.Should().BeEmpty();
		model.SelectableNames.Should().BeEmpty();
		model.TotalCount.Should().Be(0);
		model.HiddenSelectedCount(new RepositorySelection()).Should().Be(0);
	}

	[Fact]
	public void NoCategoryWithAFailure_ProducesNoCategoryColumns()
	{
		var model = Build([Row("A"), Row("B")]);

		model.Categories.Should().BeEmpty();
		model.Rows.Should().HaveCount(2);
	}
}
