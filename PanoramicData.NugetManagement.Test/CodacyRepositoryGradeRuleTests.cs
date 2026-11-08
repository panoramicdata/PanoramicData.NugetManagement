using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="CodacyRepositoryGradeRule"/> (CQ-08): a published package must hold Codacy's
/// repository grade at the configured level, and anything below it fails as an error.
/// </summary>
public class CodacyRepositoryGradeRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task IsNotApplicable_WhenCodacyIsNotConfigured()
	{
		var result = await Rule(Graded(100, "A")).EvaluateAsync(Context(codacy: null), TestContext.Current.CancellationToken);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task Passes_WhenTheRepositoryIsGradedA()
	{
		var result = await Rule(Graded(97, "A")).EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
		result.Message.Should().Contain("A").And.Contain("97");
	}

	[Fact]
	public async Task FailsAsAnError_WhenTheRepositoryIsGradedBelowA()
	{
		// Values from Jira.Api's Codacy repository endpoint on 2026-11-08: grade 86, letter B.
		var result = await Rule(Graded(86, "B")).EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Error);
		result.Message.Should().Contain("B").And.Contain("86").And.Contain("A");
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Summary.Should().NotBeNullOrWhiteSpace();
		result.Advisory.Data["grade"].Should().Be(86);
		result.Advisory.Data["grade_letter"].Should().Be("B");
	}

	[Fact]
	public async Task Passes_WhenTheGradeMeetsALowerConfiguredMinimum()
	{
		var result = await Rule(Graded(86, "B")).EvaluateAsync(Context(Token(CodacyLevel.B)), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task FailsAsAnError_WhenCodacyHasNoAnalysisOfTheDefaultBranch()
	{
		// Rundeck.Cli on 2026-11-08: GitHub's default branch was main, Codacy still graded master,
		// and asking Codacy for main answered "Unable to find branch".
		var grade = new CodacyRepositoryGrade { IsTracked = true, IsBranchAnalysed = false, CodacyDefaultBranch = "master" };

		var result = await Rule(grade).EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Error);
		result.Message.Should().Contain("main").And.Contain("master");
	}

	[Fact]
	public async Task Passes_WhenCodacyCannotBeReached()
	{
		var result = await new CodacyRepositoryGradeRule(new ThrowingService())
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
		result.Message.Should().Contain("Codacy unreachable");
	}

	[Fact]
	public async Task Passes_WhenTheRepositoryIsNotTrackedByCodacy()
	{
		var result = await Rule(new CodacyRepositoryGrade { IsTracked = false })
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Passes_WhenCodacyHasNotGradedTheBranchYet()
	{
		var result = await Rule(new CodacyRepositoryGrade { IsTracked = true })
			.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public void IsRegisteredAsAnErrorThatCodacyGradesRemotely()
	{
		var rule = RuleRegistry.Rules.Single(r => r.RuleId == "CQ-08");

		rule.Severity.Should().Be(AssessmentSeverity.Error);
		rule.Should().BeAssignableTo<IRemotelyGraded>();
	}

	private static CodacyRepositoryGradeRule Rule(CodacyRepositoryGrade grade) => new(new FakeService(grade));

	private static CodacyRepositoryGrade Graded(int grade, string letter)
		=> new() { IsTracked = true, Grade = grade, GradeLetter = letter };

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

	private sealed class FakeService(CodacyRepositoryGrade grade) : ICodacyRepositoryGradeService
	{
		public Task<CodacyRepositoryGrade> GetGradeAsync(string apiToken, string organizationName, string repositoryName, string? branch, CancellationToken cancellationToken)
			=> Task.FromResult(grade);
	}

	private sealed class ThrowingService : ICodacyRepositoryGradeService
	{
		public Task<CodacyRepositoryGrade> GetGradeAsync(string apiToken, string organizationName, string repositoryName, string? branch, CancellationToken cancellationToken)
			=> throw new InvalidOperationException("Codacy unreachable");
	}
}
