using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for CQ-03, which asks one question only: is this repository set up in Codacy and has Codacy
/// actually analysed it? What Codacy then found is CQ-05's business (issues) and CQ-06's (file
/// grades). Folding all three into this rule made two rules fire on the same ten issues and reported
/// "minimum file grade F, total issues 0" — a sentence that contradicts itself to anyone reading the
/// Codacy issues page.
///
/// A badge in a README says somebody once set Codacy up; it says nothing about the code today.
/// Treating it as a pass hid a token that could not see a single repository, with 69 repositories
/// reporting green on that basis.
/// </summary>
public class CodacyGateTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _badgeReadme = """
		# Acme.Lib

		[![Codacy Badge](https://app.codacy.com/project/badge/Grade/abc123)](https://app.codacy.com/gh/acme/Acme.Lib)
		""";

	[Fact]
	public async Task ShouldBeInconclusive_WhenTheApiFailsButABadgeIsPresent()
	{
		// An unreachable API is what a bad token looks like, and it must not be reported as compliance.
		var result = await Rule(new ThrowingService()).EvaluateAsync(
			CreateContext(withBadge: true, withApiToken: true),
			TestContext.Current.CancellationToken);

		result.IsApplicable.Should().BeFalse("the gate was never evaluated");
		result.Message.Should().Contain("could not be evaluated");
	}

	[Fact]
	public async Task ShouldStillFail_WhenTheApiFailsAndThereIsNoCodacyEvidenceAtAll()
	{
		// Absence of a .codacy.yml or badge is a fact that needs no API call, so it stays a failure.
		var result = await Rule(new ThrowingService()).EvaluateAsync(
			CreateContext(withBadge: false, withApiToken: true),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.IsApplicable.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldPassOnLocalEvidence_WhenNoApiTokenIsConfiguredAtAll()
	{
		// Without a token there is no API path to fail: the rule is a file check, and a badge is the
		// evidence it asks for. This is the one case where local evidence is the whole standard.
		var result = await Rule(new ThrowingService()).EvaluateAsync(
			CreateContext(withBadge: true, withApiToken: false),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
		result.IsApplicable.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldFail_WhenCodacyDoesNotKnowTheRepository()
	{
		// Codacy answers the file listing with a 404 for a repository that was never added, and a
		// badge in the README does not change that.
		var result = await Rule(Report(isTracked: false)).EvaluateAsync(
			CreateContext(withBadge: true, withApiToken: true),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.IsApplicable.Should().BeTrue();
		result.Message.Should().Contain("not");
	}

	[Fact]
	public async Task ShouldFail_WhenTheRepositoryIsAddedButNothingHasBeenAnalysed()
	{
		// Added to Codacy but never scanned looks identical to configured from the outside, and every
		// quality figure drawn from it would be an absence rather than a measurement.
		var result = await Rule(Report(isTracked: true, Ungraded("README.md"), Ungraded("logo.png"))).EvaluateAsync(
			CreateContext(withBadge: true, withApiToken: true),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("analys");
	}

	[Fact]
	public async Task ShouldPass_WhenCodacyHasAnalysedTheRepository()
	{
		var result = await Rule(Report(isTracked: true, Graded("src/A.cs", "A"))).EvaluateAsync(
			CreateContext(withBadge: false, withApiToken: true),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldPass_WhenAnalysisFoundPoorlyGradedFiles()
	{
		// The repository is set up and analysed, which is all this rule asks. That a file grades F is
		// CQ-06's finding and informational; it must not come back out of this rule as a failure too.
		var result = await Rule(Report(isTracked: true, Graded("src/Bad.cs", "F"))).EvaluateAsync(
			CreateContext(withBadge: false, withApiToken: true),
			TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task ShouldNameTheLastAnalysedCommit_WhenCodacyHasAnalysedTheRepository()
	{
		// "Codacy is configured and has analysed main" is an undated claim. Which commit, and when, is
		// the fact a reader needs to know whether the grades in CQ-06 are worth acting on.
		var result = await Rule(Report(
			isTracked: true,
			state: Analysed("abc1234def567"),
			Graded("src/A.cs", "A")))
			.EvaluateAsync(CreateContext(withBadge: false, withApiToken: true), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
		result.Message.Should().Contain("abc1234");
	}

	[Fact]
	public async Task ShouldStillPassAndSaySoWhenAnAnalysisIsInFlight()
	{
		// A running analysis is not a configuration problem — the integration is working, which is all
		// this rule asks. It must not fail, and it must say what is happening.
		var result = await Rule(Report(isTracked: true, state: Analysing(), Graded("src/A.cs", "A")))
			.EvaluateAsync(CreateContext(withBadge: false, withApiToken: true), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
		result.Message.Should().Contain("re-analysing");
	}

	[Fact]
	public async Task ShouldSayNothingAboutTheCommit_WhenTheAnalysisStateIsUnavailable()
	{
		var result = await Rule(Report(isTracked: true, state: null, Graded("src/A.cs", "A")))
			.EvaluateAsync(CreateContext(withBadge: false, withApiToken: true), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
		result.Message.Should().NotContain("commit");
	}

	[Fact]
	public async Task ShouldNotBlameTheIntegration_WhenTheFirstAnalysisIsStillRunning()
	{
		// A repository added minutes ago has nothing graded yet, and this rule read that as "analysed
		// nothing" and told the reader to go and run an analysis that was already running. Waiting is
		// not a misconfiguration.
		var result = await Rule(Report(isTracked: true, state: Analysing()))
			.EvaluateAsync(CreateContext(withBadge: false, withApiToken: true), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue("an analysis in flight proves the integration works");
		result.Message.Should().Contain("re-analysing");
	}

	[Fact]
	public async Task ShouldStillBlameTheIntegration_WhenNothingIsRunningAndNothingIsGraded()
	{
		// The original finding has to survive: a repository added and left unanalysed is a real
		// problem, and an in-flight analysis is the only thing that excuses it.
		var result = await Rule(Report(isTracked: true, state: Analysed("abc1234")))
			.EvaluateAsync(CreateContext(withBadge: false, withApiToken: true), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("analysed nothing");
	}

	private static CodacyAnalysisState Analysed(string sha)
		=> new()
		{
			IsAnalysing = false,
			AnalysedSha = sha,
			AnalysedAtUtc = DateTimeOffset.UtcNow.AddHours(-3),
			RetrievedAtUtc = DateTimeOffset.UtcNow
		};

	private static CodacyAnalysisState Analysing()
		=> new()
		{
			IsAnalysing = true,
			ProgressPercent = 60,
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
			RetrievedAtUtc = DateTimeOffset.UtcNow
		};

	private static CodacyConfiguredRule Rule(ICodacyFileGradeService service) => new(service);

	private static ICodacyFileGradeService Report(bool isTracked, params CodacyFileGrade[] files)
		=> new FakeService(new CodacyFileGradeReport { IsTracked = isTracked, Files = files });

	private static ICodacyFileGradeService Report(
		bool isTracked,
		CodacyAnalysisState? state,
		params CodacyFileGrade[] files)
		=> new FakeService(new CodacyFileGradeReport
		{
			IsTracked = isTracked,
			Files = files,
			AnalysisState = state
		});

	private static CodacyFileGrade Graded(string path, string gradeLetter)
		=> new() { Path = path, GradeLetter = gradeLetter, Grade = 100 };

	private static CodacyFileGrade Ungraded(string path)
		=> new() { Path = path, GradeLetter = null };

	private static RepositoryContext CreateContext(bool withBadge, bool withApiToken)
	{
		var files = new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = "<Project/>",
			["README.md"] = withBadge ? _badgeReadme : "# Acme.Lib\n\nNothing to see here."
		};

		var options = new RepoOptions();
		if (withApiToken)
		{
			options.Codacy = new CodacyOptions { ApiToken = "test-token" };
		}

		return new RepositoryContext
		{
			FullName = "acme/Acme.Lib",
			Name = "Acme.Lib",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = options,
			FilePaths = [.. files.Keys],
			FileContents = files
		};
	}

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
