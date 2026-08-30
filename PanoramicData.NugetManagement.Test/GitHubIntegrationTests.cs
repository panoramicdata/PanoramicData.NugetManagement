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
	private static readonly string[] _excludedPathPrefixes = ["PanoramicData.NugetManagement.Test/Fixtures/"];

	/// <summary>
	/// The rules whose verdict depends on how long ago somebody else published, rather than on
	/// anything in this repository.
	/// </summary>
	/// <remarks>
	/// A grace period is a clock: a release nobody adopts will eventually breach it and turn this
	/// suite red with no code change. That is the rule working as intended, but "the live repository
	/// satisfies all rules" would then be an assertion about the calendar. Their results are printed
	/// so drift is still visible here.
	/// </remarks>
	private static readonly string[] _graceDependentRuleIds = ["PKG-05", "PKG-06", "PKG-07"];

	/// <summary>
	/// Whether a GitHub token is configured. Referenced by <c>SkipUnless</c> on each test so these
	/// tests are reported as skipped, not failed, on a machine with no GitHub secret configured.
	/// </summary>
	public static bool IsGitHubConfigured => GitHubIntegrationSettings.IsConfigured;

	private readonly Lazy<IGitHubClient> _lazyGitHub;
	private readonly Lazy<RepositoryContextBuilder> _lazyContextBuilder;

	// Created on first use: building a client requires the token, so constructing it eagerly would
	// throw in the constructor before a skip condition could take effect.
	private IGitHubClient _github => _lazyGitHub.Value;
	private RepositoryContextBuilder _contextBuilder => _lazyContextBuilder.Value;

	/// <summary>
	/// Initializes a new instance of the <see cref="GitHubIntegrationTests"/> class.
	/// </summary>
	/// <param name="output">The test output helper.</param>
	public GitHubIntegrationTests(ITestOutputHelper output) : base(output)
	{
		_lazyGitHub = new Lazy<IGitHubClient>(GitHubIntegrationSettings.CreateClient);
		_lazyContextBuilder = new Lazy<RepositoryContextBuilder>(
			() => new RepositoryContextBuilder(_github, CreateLogger<RepositoryContextBuilder>()));
	}

	[Fact(SkipUnless = nameof(IsGitHubConfigured), Skip = "GitHub:Token is not configured in user secrets")]
	public async Task GitHubContextBuilder_ShouldFetchExpectedFiles_ForThisRepository()
	{
		var repository = await _github.Repository.Get("panoramicdata", "PanoramicData.NugetManagement");
		var context = ExcludingFixturePaths(await _contextBuilder.BuildAsync(repository, new RepoOptions(), CancellationToken.None));

		context.FullName.Should().Be("panoramicdata/PanoramicData.NugetManagement");
		context.FileExists("README.md").Should().BeTrue();
		context.FileExists("LICENSE").Should().BeTrue();
		context.FileExists(".github/workflows/ci.yml").Should().BeTrue();
		context.GetFileContent("README.md").Should().NotBeNullOrWhiteSpace();
	}

	[Fact(SkipUnless = nameof(IsGitHubConfigured), Skip = "GitHub:Token is not configured in user secrets")]
	public async Task GitHubAssessment_ThisRepository_ShouldBeCompliant()
	{
		var repository = await _github.Repository.Get("panoramicdata", "PanoramicData.NugetManagement");
		var context = ExcludingFixturePaths(await _contextBuilder.BuildAsync(repository, new RepoOptions(), CancellationToken.None));
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

		foreach (var graced in failures.Where(r => _graceDependentRuleIds.Contains(r.RuleId)))
		{
			Output.WriteLine($"[grace] {graced.RuleId}: {graced.Message}");
		}

		failures
			.Where(r => !_graceDependentRuleIds.Contains(r.RuleId))
			.Should().BeEmpty("the live panoramicdata/PanoramicData.NugetManagement repository should satisfy all assessment rules");
	}

	[Fact(SkipUnless = nameof(IsGitHubConfigured), Skip = "GitHub:Token is not configured in user secrets")]
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
		results.Count(r => !r.Passed && r.Severity is AssessmentSeverity.Critical or AssessmentSeverity.Error)
			   .Should().BeGreaterThan(0, "the live FailArmy repository should have at least one critical- or error-level failure");
	}

	private static RepositoryContext ExcludingFixturePaths(RepositoryContext context)
	{
		var filteredPaths = context.FilePaths
			.Where(path => !_excludedPathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
			.ToList();

		var filteredContents = context.FileContents
			.Where(kvp => !_excludedPathPrefixes.Any(prefix => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
			.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

		return new RepositoryContext
		{
			FullName = context.FullName,
			Name = context.Name,
			DefaultBranch = context.DefaultBranch,
			Options = context.Options,
			FilePaths = filteredPaths,
			FileContents = filteredContents
		};
	}
}
