using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the coverage ratchet: each repository's best figure becomes its floor, so coverage is
/// asked to increase without inventing an estate-wide threshold that would suit no repository.
/// </summary>
public class CoverageBaselineTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void ABetterFigureShouldRaiseTheFloor()
	{
		var catalog = new CoverageBaselineCatalog(null);
		catalog.Observe("acme/Widget", new CoverageBaseline(40, 30));

		catalog.Observe("acme/Widget", new CoverageBaseline(45, 35)).Should().BeTrue();
		catalog.GetBaseline("acme/Widget")!.Value.LinePercent.Should().Be(45);
	}

	[Fact]
	public void AWorseFigureShouldLeaveTheFloorAlone()
	{
		var catalog = new CoverageBaselineCatalog(null);
		catalog.Observe("acme/Widget", new CoverageBaseline(40, 30));

		catalog.Observe("acme/Widget", new CoverageBaseline(35, 25)).Should().BeFalse();
		catalog.GetBaseline("acme/Widget")!.Value.LinePercent.Should().Be(40);
	}

	[Fact]
	public void EachFigureShouldRatchetIndependently()
	{
		// Branch coverage can improve while line coverage holds; neither should drag the other down.
		var catalog = new CoverageBaselineCatalog(null);
		catalog.Observe("acme/Widget", new CoverageBaseline(40, 30));

		catalog.Observe("acme/Widget", new CoverageBaseline(40, 36));

		var baseline = catalog.GetBaseline("acme/Widget")!.Value;
		baseline.LinePercent.Should().Be(40);
		baseline.BranchPercent.Should().Be(36);
	}

	[Fact]
	public async Task TST07_ShouldReportNotApplicable_WhenNothingHasBeenMeasured()
	{
		var result = await Rule(new CoverageBaselineCatalog(null))
			.EvaluateAsync(Context(line: null, branch: null), CancellationToken.None);

		result.IsApplicable.Should().BeFalse("an unmeasured repository has demonstrated nothing");
	}

	[Fact]
	public async Task TST07_ShouldRecordTheFirstMeasurementAsTheFloor()
	{
		var catalog = new CoverageBaselineCatalog(null);

		var result = await Rule(catalog).EvaluateAsync(Context(33.8, 22.8), CancellationToken.None);

		result.Passed.Should().BeTrue();
		catalog.GetBaseline("test-org/Acme.Widget")!.Value.LinePercent.Should().Be(33.8);
	}

	[Fact]
	public async Task TST07_ShouldReportADropBelowTheFloor()
	{
		var catalog = new CoverageBaselineCatalog(null);
		catalog.Observe("test-org/Acme.Widget", new CoverageBaseline(40, 30));

		var result = await Rule(catalog).EvaluateAsync(Context(35, 30), CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("35.0").And.Contain("40.0");
	}

	[Fact]
	public async Task TST07_ShouldBeInformationalOnly()
		=> (await Rule(new CoverageBaselineCatalog(null)).EvaluateAsync(Context(1, 1), CancellationToken.None))
			.Severity.Should().Be(AssessmentSeverity.Info,
				"coverage is a direction of travel while the estate climbs, not a gate");

	private static CodeCoverageTrendRule Rule(CoverageBaselineCatalog catalog) => new(catalog);

	private static RepositoryContext Context(double? line, double? branch) => new()
	{
		FullName = "test-org/Acme.Widget",
		Name = "Acme.Widget",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = ["Acme.Widget.Test/Acme.Widget.Test.csproj"],
		FileContents = new() { ["Acme.Widget.Test/Acme.Widget.Test.csproj"] = "<Project/>" },
		LineCoveragePercent = line,
		BranchCoveragePercent = branch
	};
}
