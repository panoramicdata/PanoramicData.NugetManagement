using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="CodacyIssuesRule"/> (CQ-05) using a fake Codacy issue service.
/// </summary>
public class CodacyIssuesRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
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

	private static CodacyOptions Token(int maxIssueCount = 0)
		=> new() { ApiToken = "test-token", MaxIssueCount = maxIssueCount };

	private static List<CodacyIssue> SampleIssues(int count)
		=> [.. Enumerable.Range(1, count).Select(i => new CodacyIssue
		{
			FilePath = $"src/File{i}.cs",
			Line = i,
			Message = $"Issue {i}",
			PatternId = i % 2 == 0 ? "SonarCSharp_S2360" : "SonarCSharp_S121",
			Category = "BestPractice",
			Severity = "Warning",
			Language = "CSharp"
		})];

	[Fact]
	public async Task Passes_WhenCodacyNotConfigured()
	{
		var rule = new CodacyIssuesRule(new FakeService(new CodacyRepositoryReport { IsTracked = true, Issues = SampleIssues(5) }));
		var result = await rule.EvaluateAsync(Context(codacy: null), TestContext.Current.CancellationToken);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Passes_WhenRepositoryNotTrackedByCodacy()
	{
		var rule = new CodacyIssuesRule(new FakeService(new CodacyRepositoryReport { IsTracked = false }));
		var result = await rule.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task Passes_WhenNoIssues()
	{
		var rule = new CodacyIssuesRule(new FakeService(new CodacyRepositoryReport { IsTracked = true, Issues = [] }));
		var result = await rule.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task WarnsWithFullDetail_WhenIssuesWithinBudget()
	{
		var rule = new CodacyIssuesRule(new FakeService(new CodacyRepositoryReport { IsTracked = true, Issues = SampleIssues(4) }));
		var result = await rule.EvaluateAsync(Context(Token(maxIssueCount: 0)), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Warning);
		result.Advisory.Should().NotBeNull();
		// Full detail must list every issue location so an AI session need not re-fetch from Codacy.
		result.Advisory!.Detail.Should().Contain("src/File1.cs:1");
		result.Advisory.Detail.Should().Contain("src/File4.cs:4");
		result.Advisory.Data.Should().ContainKey("total_issues");
		result.Advisory.Data["total_issues"].Should().Be(4);
	}

	[Fact]
	public async Task EscalatesToError_WhenBudgetBreached()
	{
		var rule = new CodacyIssuesRule(new FakeService(new CodacyRepositoryReport { IsTracked = true, Issues = SampleIssues(5) }));
		var result = await rule.EvaluateAsync(Context(Token(maxIssueCount: 2)), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Error);
		result.Message.Should().Contain("budget");
	}

	[Fact]
	public async Task Passes_WhenServiceThrows_NonBlocking()
	{
		var rule = new CodacyIssuesRule(new ThrowingService());
		var result = await rule.EvaluateAsync(Context(Token()), TestContext.Current.CancellationToken);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public void IsGradedRemotely_SoAFixIsNeverJudgedByReRunningIt()
		=> new CodacyIssuesRule(new ThrowingService())
			.Should().BeAssignableTo<PanoramicData.NugetManagement.Rules.IRemotelyGraded>(
				"the issue list comes from Codacy's reading of the published branch, so an edit to the "
				+ "clone cannot change this rule's answer and a fix loop that waits for it never ends");

	/// <summary>
	/// The marker means "editing a file locally cannot change this rule's answer", and only that. A
	/// rule that reads the working tree must never carry it, however much of the network it also uses.
	/// </summary>
	[Fact]
	public void RulesThatReadTheWorkingTree_AreNotMarkedRemotelyGraded()
		=> PanoramicData.NugetManagement.Services.RuleRegistry.Rules
			.Where(rule => rule is PanoramicData.NugetManagement.Rules.IRemotelyGraded)
			.Select(rule => rule.RuleId)
			.Should().BeEquivalentTo(["CQ-05", "CQ-06"]);

	private sealed class FakeService(CodacyRepositoryReport report) : ICodacyIssueService
	{
		public Task<CodacyRepositoryReport> GetReportAsync(string apiToken, string organizationName, string repositoryName, string? branch, CancellationToken cancellationToken)
			=> Task.FromResult(report);
	}

	private sealed class ThrowingService : ICodacyIssueService
	{
		public Task<CodacyRepositoryReport> GetReportAsync(string apiToken, string organizationName, string repositoryName, string? branch, CancellationToken cancellationToken)
			=> throw new InvalidOperationException("Codacy unreachable");
	}
}
