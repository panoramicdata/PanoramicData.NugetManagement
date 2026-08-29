using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the navigation tree's status roll-up: a parent node shows the worst status beneath it,
/// and Unknown outranks every other status — an unassessed repository could be in any state at all,
/// so a green branch above one would claim something we cannot know.
/// </summary>
public class NavHealthRollupTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void Worst_UnknownBeatsError()
		=> NavHealthRollup.Worst([PackageHealthStatus.Error, PackageHealthStatus.Unknown, PackageHealthStatus.Success])
			.Should().Be(PackageHealthStatus.Unknown);

	[Fact]
	public void Worst_ErrorBeatsWarning()
		=> NavHealthRollup.Worst([PackageHealthStatus.Success, PackageHealthStatus.Warning, PackageHealthStatus.Error])
			.Should().Be(PackageHealthStatus.Error);

	[Fact]
	public void Worst_WarningBeatsInfo()
		=> NavHealthRollup.Worst([PackageHealthStatus.Info, PackageHealthStatus.Warning])
			.Should().Be(PackageHealthStatus.Warning);

	[Fact]
	public void Worst_InfoBeatsSuccess()
		=> NavHealthRollup.Worst([PackageHealthStatus.Success, PackageHealthStatus.Info])
			.Should().Be(PackageHealthStatus.Info);

	[Fact]
	public void Worst_AllSuccessIsSuccess()
		=> NavHealthRollup.Worst([PackageHealthStatus.Success, PackageHealthStatus.Success])
			.Should().Be(PackageHealthStatus.Success);

	[Fact]
	public void Worst_PendingRanksBelowUnknownAndAboveError()
	{
		NavHealthRollup.Worst([PackageHealthStatus.Pending, PackageHealthStatus.Unknown])
			.Should().Be(PackageHealthStatus.Unknown);

		NavHealthRollup.Worst([PackageHealthStatus.Error, PackageHealthStatus.Pending])
			.Should().Be(PackageHealthStatus.Pending);
	}

	[Fact]
	public void Worst_NothingToRollUpIsUnknown()
		=> NavHealthRollup.Worst([]).Should().Be(PackageHealthStatus.Unknown);

	[Fact]
	public void ColourClass_UnknownAndPendingAreGrey()
	{
		NavHealthRollup.ColourClass(PackageHealthStatus.Unknown).Should().Be("text-muted");
		NavHealthRollup.ColourClass(PackageHealthStatus.Pending).Should().Be("text-muted");
	}

	[Fact]
	public void ColourClass_CoversEveryOtherStatus()
	{
		NavHealthRollup.ColourClass(PackageHealthStatus.Error).Should().Be("text-danger");
		NavHealthRollup.ColourClass(PackageHealthStatus.Warning).Should().Be("text-warning");
		NavHealthRollup.ColourClass(PackageHealthStatus.Info).Should().Be("text-info");
		NavHealthRollup.ColourClass(PackageHealthStatus.Success).Should().Be("text-success");
	}

	[Fact]
	public void Icon_AppendsColourToGlyph()
		=> NavHealthRollup.Icon("fas fa-cubes", PackageHealthStatus.Unknown)
			.Should().Be("fas fa-cubes text-muted");

	[Fact]
	public void ForRepositories_OneUnknownRepositoryGreysTheBranch()
	{
		List<PackageDashboardRow> rows =
		[
			Assessed("Clean.Api", []),
			Unassessed("Mystery.Api")
		];

		NavHealthRollup.ForRepositories(rows).Should().Be(PackageHealthStatus.Unknown);
	}

	[Fact]
	public void ForRepositories_AllAssessedTakesTheWorstSeverity()
	{
		List<PackageDashboardRow> rows =
		[
			Assessed("Clean.Api", []),
			Assessed("Warned.Api", [Failure(AssessmentSeverity.Warning)]),
			Assessed("Broken.Api", [Failure(AssessmentSeverity.Error)])
		];

		NavHealthRollup.ForRepositories(rows).Should().Be(PackageHealthStatus.Error);
	}

	[Fact]
	public void ForRepositories_AllCleanIsGreen()
	{
		List<PackageDashboardRow> rows = [Assessed("Clean.Api", []), Assessed("AlsoClean.Api", [])];

		NavHealthRollup.ForRepositories(rows).Should().Be(PackageHealthStatus.Success);
	}

	[Fact]
	public void ForRepositories_NoRowsIsUnknown()
		=> NavHealthRollup.ForRepositories(null).Should().Be(PackageHealthStatus.Unknown);

	[Fact]
	public void ForIssues_UnassessedRepositoryDoesNotGreyTheBranch()
	{
		List<PackageDashboardRow> rows = [Assessed("Clean.Api", []), Unassessed("Mystery.Api")];

		NavHealthRollup.ForIssues(rows, []).Should().Be(PackageHealthStatus.Success);
	}

	[Fact]
	public void ForIssues_UnassessedRepositoryDoesNotOutrankACategorySeverity()
	{
		List<PackageDashboardRow> rows = [Assessed("Broken.Api", [Failure(AssessmentSeverity.Warning)]), Unassessed("Mystery.Api")];

		NavHealthRollup.ForIssues(rows, [AssessmentSeverity.Warning]).Should().Be(PackageHealthStatus.Warning);
	}

	[Fact]
	public void ForIssues_NothingAssessedYetIsUnknown()
	{
		List<PackageDashboardRow> rows = [Unassessed("Mystery.Api")];

		NavHealthRollup.ForIssues(rows, []).Should().Be(PackageHealthStatus.Unknown);
	}

	[Fact]
	public void ForIssues_EverythingAssessedAndCleanIsGreen()
	{
		List<PackageDashboardRow> rows = [Assessed("Clean.Api", [])];

		NavHealthRollup.ForIssues(rows, []).Should().Be(PackageHealthStatus.Success);
	}

	[Fact]
	public void ForIssues_TakesTheWorstCategorySeverity()
	{
		List<PackageDashboardRow> rows = [Assessed("Broken.Api", [Failure(AssessmentSeverity.Error)])];

		NavHealthRollup.ForIssues(rows, [AssessmentSeverity.Warning, AssessmentSeverity.Critical])
			.Should().Be(PackageHealthStatus.Error);
	}

	[Fact]
	public void ForIssues_NoRowsIsUnknown()
		=> NavHealthRollup.ForIssues(null, []).Should().Be(PackageHealthStatus.Unknown);

	[Fact]
	public void FromSeverity_CriticalAndErrorAreBothRed()
	{
		NavHealthRollup.FromSeverity(AssessmentSeverity.Critical).Should().Be(PackageHealthStatus.Error);
		NavHealthRollup.FromSeverity(AssessmentSeverity.Error).Should().Be(PackageHealthStatus.Error);
		NavHealthRollup.FromSeverity(AssessmentSeverity.Warning).Should().Be(PackageHealthStatus.Warning);
		NavHealthRollup.FromSeverity(AssessmentSeverity.Info).Should().Be(PackageHealthStatus.Info);
	}

	private static PackageDashboardRow Unassessed(string packageId)
		=> new() { PackageId = packageId };

	private static PackageDashboardRow Assessed(string packageId, List<RuleResult> failures)
		=> new()
		{
			PackageId = packageId,
			Assessment = new RepoAssessment
			{
				RepositoryFullName = $"panoramicdata/{packageId}",
				DefaultBranch = "main",
				AssessedAtUtc = DateTimeOffset.UnixEpoch,
				RuleResults = failures
			}
		};

	private static RuleResult Failure(AssessmentSeverity severity)
		=> new()
		{
			RuleId = $"TST-{(int)severity}",
			RuleName = "Test rule",
			Category = AssessmentCategory.NuGetHygiene,
			Severity = severity,
			Passed = false,
			Message = "failed"
		};
}
