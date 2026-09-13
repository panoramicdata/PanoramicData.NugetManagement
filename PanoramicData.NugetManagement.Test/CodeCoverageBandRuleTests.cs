using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// The four-band coverage grade, TST-10.
/// </summary>
public class CodeCoverageBandRuleTests
{
	// Mirrors the Context helper in CoverageBaselineTests, which is the proven shape for this type.
	// FileContents is a Dictionary, so it needs `new() { ... }` — a collection expression does not
	// compile here.
	private static RepositoryContext ContextWith(double? coverage, bool hasTestProject = true)
		=> new()
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = hasTestProject
				? ["Acme.Widget/Acme.Widget.csproj", "Acme.Widget.Test/Acme.Widget.Test.csproj"]
				: ["Acme.Widget/Acme.Widget.csproj"],
			FileContents = hasTestProject
				? new() { ["Acme.Widget.Test/Acme.Widget.Test.csproj"] = "<Project/>" }
				: new() { ["Acme.Widget/Acme.Widget.csproj"] = "<Project/>" },
			LineCoveragePercent = coverage
		};

	private static async Task<RuleResult> EvaluateAsync(double? coverage, bool hasTestProject = true)
		=> await new CodeCoverageBandRule()
			.EvaluateAsync(ContextWith(coverage, hasTestProject), CancellationToken.None)
			.ConfigureAwait(false);

	[Fact]
	public async Task NoTestProjects_IsNotApplicable()
	{
		var result = await EvaluateAsync(coverage: 90, hasTestProject: false);

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue();
	}

	[Theory]
	[InlineData(null)]
	[InlineData(0d)]
	public async Task NoCoverage_IsRed(double? coverage)
	{
		var result = await EvaluateAsync(coverage);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Error);
		result.Advisory.Should().NotBeNull();
	}

	[Theory]
	[InlineData(0.1)]
	[InlineData(29.9)]
	public async Task BelowThirty_IsAmber(double coverage)
	{
		var result = await EvaluateAsync(coverage);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Warning);
		result.Advisory.Should().NotBeNull();
	}

	[Theory]
	[InlineData(30d)]
	[InlineData(59.9)]
	public async Task BetweenThirtyAndSixty_IsBlue(double coverage)
	{
		var result = await EvaluateAsync(coverage);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Info);
		result.Advisory.Should().NotBeNull();
	}

	[Theory]
	[InlineData(60d)]
	[InlineData(100d)]
	public async Task AtLeastSixty_IsGreen(double coverage)
	{
		var result = await EvaluateAsync(coverage);

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeTrue();
	}

	[Fact]
	public async Task TheAdvisoryCarriesTheMeasuredFigureAndBand()
	{
		var result = await EvaluateAsync(12.5);

		result.Advisory!.Data["measured_line"].Should().Be(12.5);
		result.Advisory.Data["band"].Should().Be("AMBER");
	}

	[Fact]
	public void RuleIdIsTst10()
		=> new CodeCoverageBandRule().RuleId.Should().Be("TST-10");
}
