using Microsoft.Extensions.Logging;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// End-to-end integration tests against live GitHub repositories.
/// </summary>
public class GitHubIntegrationTests : TestWithOutput
{
	private readonly IGitHubClient _github;
	private readonly RepositoryContextBuilder _contextBuilder;

	/// <summary>
	/// Initializes a new instance of the <see cref="GitHubIntegrationTests"/> class.
	/// </summary>
	/// <param name="output">The test output helper.</param>
	public GitHubIntegrationTests(ITestOutputHelper output) : base(output)
	{
		_github = GitHubIntegrationSettings.CreateClient();
		_contextBuilder = new RepositoryContextBuilder(_github, CreateLogger<RepositoryContextBuilder>());
	}

	[Fact]
	public async Task GitHubContextBuilder_ShouldFetchExpectedFiles_ForThisRepository()
	{
		var repository = await _github.Repository.Get("panoramicdata", "PanoramicData.NugetManagement");
		var context = await _contextBuilder.BuildAsync(repository, new RepoOptions(), CancellationToken.None);

		context.FullName.Should().Be("panoramicdata/PanoramicData.NugetManagement");
		context.FileExists("README.md").Should().BeTrue();
		context.FileExists("LICENSE").Should().BeTrue();
		context.FileExists(".github/workflows/ci.yml").Should().BeTrue();
		context.GetFileContent("README.md").Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task GitHubAssessment_ThisRepository_ShouldBeCompliant()
	{
		var repository = await _github.Repository.Get("panoramicdata", "PanoramicData.NugetManagement");
		var context = await _contextBuilder.BuildAsync(repository, new RepoOptions(), CancellationToken.None);
		var failures = new List<RuleResult>();

		foreach (var rule in RuleRegistry.Rules)
		{
			var result = await rule.EvaluateAsync(context, CancellationToken.None);
			Output.WriteLine($"[{(result.Passed ? "PASS" : "FAIL")}] {result.RuleId}: {result.Message}");
			if (!result.Passed)
			{
				failures.Add(result);
			}
		}

		failures.Should().BeEmpty("the live panoramicdata/PanoramicData.NugetManagement repository should satisfy all assessment rules");
	}

	[Fact]
	public async Task GitHubAssessment_FailArmyRepository_ShouldNotBeCompliant()
	{
		var repository = await _github.Repository.Get("panoramicdata", "PanoramicData.NugetFailArmy");
		var context = await _contextBuilder.BuildAsync(repository, new RepoOptions(), CancellationToken.None);

		var results = new List<RuleResult>();
		foreach (var rule in RuleRegistry.Rules)
		{
			results.Add(await rule.EvaluateAsync(context, CancellationToken.None));
		}

		results.Should().Contain(r => !r.Passed, "the live FailArmy repository should violate multiple rules");
		results.Count(r => !r.Passed && r.Severity == AssessmentSeverity.Error)
			.Should().BeGreaterThan(0, "the live FailArmy repository should have at least one error-level failure");
	}
}