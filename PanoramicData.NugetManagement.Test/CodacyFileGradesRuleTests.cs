using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="CodacyFileGradesRule"/> (CQ-06), which reports the files Codacy grades below
/// the configured level. A file's grade also reflects duplication and complexity, so it can be poor
/// while the repository has no issues at all — which is why this is reported for information rather
/// than as a gate, and why it is separate from CQ-05's issue count.
/// </summary>
public class CodacyFileGradesRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task IsNotApplicable_WhenCodacyIsNotConfigured()
	{
		var result = await Rule(Report(File("src/A.cs", "A"))).EvaluateAsync(
			Context(codacy: null),
			TestContext.Current.CancellationToken);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task Passes_WhenEveryGradedFileMeetsTheMinimumLevel()
	{
		var result = await Rule(Report(File("src/A.cs", "A"), File("src/B.cs", "A"))).EvaluateAsync(
			Context(Token()),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ReportsForInformationOnly_WhenAFileIsBelowTheMinimumLevel()
	{
		// The whole point of splitting this out of CQ-03: a poor file grade must never fail the
		// compliance gate, so it is Info and Info alone, whatever the grade is.
		var result = await Rule(Report(File("src/A.cs", "A"), File("src/Bad.cs", "F"))).EvaluateAsync(
			Context(Token()),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Info);
	}

	[Fact]
	public async Task NamesTheFileAndItsMeasurements_WhenAFileIsBelowTheMinimumLevel()
	{
		// "minimum file grade F" told nobody which file, and looked like a contradiction next to a
		// Codacy issues page reading zero. The finding has to say what is wrong and where.
		var result = await Rule(Report(
			File("src/A.cs", "A"),
			File("Sample.Test/TaiTests.cs", "F", grade: 0, totalIssues: 0, complexity: 3, linesOfCode: 41)))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Message.Should().Contain("Sample.Test/TaiTests.cs");
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Detail.Should().Contain("Sample.Test/TaiTests.cs");
		result.Advisory.Detail.Should().Contain("41");
		result.Advisory.Data.Should().ContainKey("files_below_minimum");
		result.Advisory.Data["files_below_minimum"].Should().Be(1);
	}

	[Fact]
	public async Task IgnoresUngradedFiles_WhenTheBranchCarriesFilesCodacyNeverAnalysed()
	{
		// Codacy lists every file on the branch but grades only what it analyses. Markdown, JSON,
		// images and the solution file come back with no letter, and reading that absence as an F
		// made the worst file in every repository an F.
		var result = await Rule(Report(File("src/A.cs", "A"), File("README.md", null), File("logo.png", "")))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TreatsAnUnrecognisedLetterAsTheWorstGrade_WhenCodacyReturnsALetterWeDoNotKnow()
	{
		// A letter we cannot parse is a grade we do not understand, not a good one. Distinct from the
		// blank case above, which means "not analysed".
		var result = await Rule(Report(File("src/Odd.cs", "Z"))).EvaluateAsync(
			Context(Token()),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("src/Odd.cs");
	}

	[Fact]
	public async Task Passes_WhenCodacyDoesNotTrackTheRepository()
	{
		// Whether the repository is in Codacy at all is CQ-03's question, and it must be answered in
		// one place only.
		var result = await Rule(new CodacyFileGradeReport { IsTracked = false }).EvaluateAsync(
			Context(Token()),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Passes_WhenCodacyIsUnreachable()
	{
		// An unreachable Codacy leaves grades unknown. CQ-03 is the rule that reports a broken
		// integration; this one stays quiet rather than inventing a second alarm for it.
		var result = await new CodacyFileGradesRule(new ThrowingService()).EvaluateAsync(
			Context(Token()),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task HonoursAConfiguredMinimumLevel_WhenTheRepositoryAcceptsLowerGrades()
	{
		var result = await Rule(Report(File("src/B.cs", "B"))).EvaluateAsync(
			Context(Token(minimumLevel: CodacyLevel.C)),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	private static CodacyFileGradesRule Rule(CodacyFileGradeReport report) => new(new FakeService(report));

	private static CodacyFileGradeReport Report(params CodacyFileGrade[] files)
		=> new() { IsTracked = true, Files = files };

	private static CodacyFileGrade File(
		string path,
		string? gradeLetter,
		int grade = 100,
		int totalIssues = 0,
		int? complexity = null,
		int? linesOfCode = null)
		=> new()
		{
			Path = path,
			GradeLetter = gradeLetter,
			Grade = grade,
			TotalIssues = totalIssues,
			Complexity = complexity,
			LinesOfCode = linesOfCode
		};

	private static CodacyOptions Token(CodacyLevel minimumLevel = CodacyLevel.A)
		=> new() { ApiToken = "test-token", MinimumLevel = minimumLevel };

	private static RepositoryContext Context(CodacyOptions? codacy)
		=> new()
		{
			FullName = "panoramicdata/Sample.Api",
			Name = "Sample.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions { Codacy = codacy },
			FilePaths = [],
			FileContents = []
		};

	private sealed class FakeService(CodacyFileGradeReport report) : ICodacyFileGradeService
	{
		public Task<CodacyFileGradeReport> GetGradesAsync(string apiToken, string organizationName, string repositoryName, string? branch, CancellationToken cancellationToken)
			=> Task.FromResult(report);
	}

	private sealed class ThrowingService : ICodacyFileGradeService
	{
		public Task<CodacyFileGradeReport> GetGradesAsync(string apiToken, string organizationName, string repositoryName, string? branch, CancellationToken cancellationToken)
			=> throw new InvalidOperationException("Codacy unreachable");
	}
}
