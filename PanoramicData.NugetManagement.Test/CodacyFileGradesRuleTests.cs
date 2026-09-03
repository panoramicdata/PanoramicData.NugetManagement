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
			File("Sample.Test/TaiTests.cs", "F", grade: 0, totalIssues: 0, complexity: 3, linesOfCode: 41, duplication: 36, numberOfClones: 5)))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Message.Should().Contain("Sample.Test/TaiTests.cs");
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Detail.Should().Contain("Sample.Test/TaiTests.cs");
		result.Advisory.Detail.Should().Contain("41");
		result.Advisory.Data.Should().ContainKey("files_below_minimum");
		result.Advisory.Data["files_below_minimum"].Should().Be(1);
	}

	[Fact]
	public async Task NamesDuplicationAsTheCause_WhenThatIsWhatDrivesTheGrade()
	{
		// The whole reason CQ-06 exists: a reader looking at a Codacy issues page reading zero needs
		// to be told the grade came from duplication, or the finding looks like a mistake.
		var result = await Rule(Report(
			File("Sample.Test/TaiTests.cs", "F", grade: 0, totalIssues: 0, duplication: 36, numberOfClones: 5)))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Message.Should().Contain("36% duplication");
		result.Advisory!.Detail.Should().Contain("36%");
		result.Advisory.Data["files"].Should().NotBeNull();
	}

	[Fact]
	public async Task NamesTheIssueCountAsTheCause_WhenTheGradeComesFromIssues()
	{
		var result = await Rule(Report(File("src/Messy.cs", "D", totalIssues: 7)))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Message.Should().Contain("7 issue(s)");
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

	[Fact]
	public async Task ListsTheIssuesBehindTheGrade_WhenCodacyKnowsThem()
	{
		// A grade says a file is poor and no more. Told "Publish.ps1, 9 issues" and nothing else, a
		// model spends its whole budget guessing which nine — which is exactly what one did.
		var result = await Rule(
			Report(File("Publish.ps1", "F", totalIssues: 2)),
			Issue("Publish.ps1", 12, "PSAvoidUsingWriteHost", "Avoid using Write-Host."),
			Issue("Publish.ps1", 40, "PSUseDeclaredVarsMoreThanAssignments", "The variable is never used."))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Advisory!.Detail.Should().Contain("PSAvoidUsingWriteHost");
		result.Advisory.Detail.Should().Contain("Avoid using Write-Host.");
		result.Advisory.Detail.Should().Contain("line 12");

		var files = (List<Dictionary<string, object?>>)result.Advisory.Data["files"]!;
		var issues = (List<Dictionary<string, object?>>)files[0]["issues"]!;

		issues.Should().HaveCount(2);
		issues[0]["pattern"].Should().Be("PSAvoidUsingWriteHost");
	}

	[Fact]
	public async Task LeavesOutIssuesForFilesThatMeetTheMinimum()
	{
		var result = await Rule(
			Report(File("src/Good.cs", "A"), File("Publish.ps1", "F")),
			Issue("src/Good.cs", 3, "SomePattern", "Something about a good file."),
			Issue("Publish.ps1", 12, "PSAvoidUsingWriteHost", "Avoid using Write-Host."))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Advisory!.Detail.Should().NotContain("Something about a good file.",
			"a file that is not being fixed is a tangent");
	}

	[Fact]
	public async Task MatchesIssuesToFiles_WhenTheTwoEndpointsSpellThePathDifferently()
	{
		// Codacy's file list and its issue list need not agree on separators or a leading "./", and a
		// path that fails to match drops a file's issues without saying so.
		var result = await Rule(
			Report(File("src/Messy.cs", "F")),
			Issue("./src\\Messy.cs", 7, "SonarCSharp_S3776", "Reduce complexity."))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Advisory!.Detail.Should().Contain("Reduce complexity.");
	}

	[Fact]
	public async Task StillReportsTheGrades_WhenTheIssuesEndpointIsUnreachable()
	{
		// The grades are the finding. Losing them because a second endpoint failed would trade the
		// whole rule for the detail, and CQ-03 already owns "the integration is broken".
		var result = await new CodacyFileGradesRule(
			new FakeService(Report(File("Publish.ps1", "F"))),
			new ThrowingIssueService())
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Advisory!.Detail.Should().Contain("Publish.ps1");
	}

	[Fact]
	public async Task NamesEachPoorFileAsItsOwnTarget_SoFixWithAiTakesThemOneAtATime()
	{
		var result = await Rule(Report(
			File("src/A.cs", "A"),
			File("Worst.ps1", "F"),
			File("src/Middling.cs", "D")))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Advisory!.Targets!.Select(target => target.Path).Should().Equal(["Worst.ps1", "src/Middling.cs"],
			"worst first, so the queue spends the first session where it counts most");
	}

	[Fact]
	public async Task CapsTheTargetsAndSaysSo_WhenAlmostEveryFileIsPoor()
	{
		// Every target is a queued item and a session on the shared GPU. Truncating silently would
		// read as "that was all of them".
		var result = await Rule(Report([.. Enumerable
			.Range(0, 14)
			.Select(i => File($"src/Bad{i}.cs", "F"))]))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Advisory!.Targets.Should().HaveCount(10);
		result.Advisory.Detail.Should().Contain("worst 10");
	}

	[Fact]
	public void IsGradedRemotely_SoAFixIsNeverJudgedByReRunningIt()
		=> new CodacyFileGradesRule(new FakeService(Report()))
			.Should().BeAssignableTo<IRemotelyGraded>(
				"Codacy grades the published branch, so editing the clone cannot change this rule's answer");

	[Fact]
	public async Task SaysCodacyIsReanalysing_WhenAnAnalysisIsInFlight()
	{
		// Without this the grades read as current fact, and the reader goes and fixes a file whose
		// grade is being recalculated as they look at it.
		var result = await Rule(Report(Analysing(progress: 60), File("src/Bad.cs", "F")))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Message.Should().Contain("re-analysing");
		result.Message.Should().Contain("60%");
	}

	[Fact]
	public async Task NamesTheCommitTheGradesDescribe_WhenCodacyIsBehindTheCheckout()
	{
		var result = await Rule(Report(Analysed("abc1234"), File("src/Bad.cs", "F")))
			.EvaluateAsync(Context(Token(), headSha: "9f8e7d6"), TestContext.Current.CancellationToken);

		result.Message.Should().Contain("abc1234");
	}

	[Fact]
	public async Task SaysNothingAboutFreshness_WhenCodacyHasAnalysedTheCheckedOutCommit()
	{
		// Forty-odd repositories are current at any time. A caveat on every one of them is noise that
		// trains the reader to ignore the caveat that matters.
		var result = await Rule(Report(Analysed("abc1234"), File("src/Bad.cs", "F")))
			.EvaluateAsync(Context(Token(), headSha: "abc1234"), TestContext.Current.CancellationToken);

		result.Message.Should().NotContain("abc1234");
		result.Message.Should().NotContain("re-analysing");
	}

	[Fact]
	public async Task SaysNothingAboutFreshness_WhenTheCheckedOutCommitIsUnknown()
	{
		// A remote-only context has no head SHA. Reporting "behind" from a comparison we cannot make
		// would invent a staleness nobody can act on.
		var result = await Rule(Report(Analysed("abc1234"), File("src/Bad.cs", "F")))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Message.Should().NotContain("abc1234");
	}

	[Fact]
	public async Task KeepsTheGradesAndTheTargets_WhenTheAnalysisStateIsUnavailable()
	{
		// Codacy's progress endpoint is a second call on a path that already worked. Losing the whole
		// finding because the caveat could not be fetched would be worse than reporting it uncaveated.
		var result = await Rule(Report(state: null, File("src/Bad.cs", "F")))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("src/Bad.cs");
		result.Advisory!.Targets.Should().HaveCount(1);
	}

	[Fact]
	public async Task KeepsTheTargets_WhenAnAnalysisIsInFlight()
	{
		// The grades are annotated, not withheld. Fix with AI still has work to offer.
		var result = await Rule(Report(Analysing(progress: 60), File("src/Bad.cs", "F")))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Advisory!.Targets.Should().HaveCount(1);
	}

	[Fact]
	public async Task WarnsTheAiSessionTheGradesMayMove_WhenAnAnalysisIsInFlight()
	{
		// A model reads the advisory table as ground truth unless told otherwise.
		var result = await Rule(Report(Analysing(progress: 60), File("src/Bad.cs", "F")))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Advisory!.Detail.Should().Contain("re-analysing");
	}

	[Fact]
	public async Task PublishesTheFreshnessFactsForMachines()
	{
		var result = await Rule(Report(Analysing(progress: 60, analysedSha: "abc1234"), File("src/Bad.cs", "F")))
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Advisory!.Data["codacy_is_analysing"].Should().Be(true);
		result.Advisory.Data["codacy_analysed_sha"].Should().Be("abc1234");
	}

	private static CodacyAnalysisState Analysing(int progress, string? analysedSha = null)
		=> new()
		{
			IsAnalysing = true,
			ProgressPercent = progress,
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
			AnalysedSha = analysedSha,
			RetrievedAtUtc = DateTimeOffset.UtcNow
		};

	private static CodacyAnalysisState Analysed(string sha)
		=> new()
		{
			IsAnalysing = false,
			AnalysedSha = sha,
			AnalysedAtUtc = DateTimeOffset.UtcNow.AddHours(-3),
			RetrievedAtUtc = DateTimeOffset.UtcNow
		};

	private static CodacyFileGradesRule Rule(CodacyFileGradeReport report, params CodacyIssue[] issues)
		=> new(new FakeService(report), new FakeIssueService(issues));

	private static CodacyIssue Issue(string path, long line, string pattern, string message)
		=> new()
		{
			FilePath = path,
			Line = line,
			PatternId = pattern,
			Message = message
		};

	private static CodacyFileGradeReport Report(params CodacyFileGrade[] files)
		=> new() { IsTracked = true, Files = files };

	private static CodacyFileGradeReport Report(CodacyAnalysisState? state, params CodacyFileGrade[] files)
		=> new() { IsTracked = true, Files = files, AnalysisState = state };

	private static CodacyFileGrade File(
		string path,
		string? gradeLetter,
		int grade = 100,
		int totalIssues = 0,
		int? complexity = null,
		int? linesOfCode = null,
		int? duplication = null,
		int? numberOfClones = null)
		=> new()
		{
			Path = path,
			GradeLetter = gradeLetter,
			Grade = grade,
			TotalIssues = totalIssues,
			Complexity = complexity,
			Duplication = duplication,
			NumberOfClones = numberOfClones,
			LinesOfCode = linesOfCode
		};

	private static CodacyOptions Token(CodacyLevel minimumLevel = CodacyLevel.A)
		=> new() { ApiToken = "test-token", MinimumLevel = minimumLevel };

	private static RepositoryContext Context(CodacyOptions? codacy, string? headSha = null)
		=> new()
		{
			FullName = "panoramicdata/Sample.Api",
			Name = "Sample.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			HeadSha = headSha,
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

	private sealed class FakeIssueService(params CodacyIssue[] issues) : ICodacyIssueService
	{
		public Task<CodacyRepositoryReport> GetReportAsync(string apiToken, string organizationName, string repositoryName, string? branch, CancellationToken cancellationToken)
			=> Task.FromResult(new CodacyRepositoryReport { IsTracked = true, Issues = issues });
	}

	private sealed class ThrowingIssueService : ICodacyIssueService
	{
		public Task<CodacyRepositoryReport> GetReportAsync(string apiToken, string organizationName, string repositoryName, string? branch, CancellationToken cancellationToken)
			=> throw new InvalidOperationException("Codacy issues unreachable");
	}
}
