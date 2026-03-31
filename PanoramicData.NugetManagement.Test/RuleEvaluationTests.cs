using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for individual rules using a synthetic RepositoryContext.
/// </summary>
public class RuleEvaluationTests : TestWithOutput
{
	/// <summary>
	/// Initializes a new instance of the <see cref="RuleEvaluationTests"/> class.
	/// </summary>
	/// <param name="output">The test output helper.</param>
	public RuleEvaluationTests(ITestOutputHelper output) : base(output)
	{
	}

	[Fact]
	public async Task AllRules_ShouldReturnResult_WhenContextIsEmpty()
	{
		var context = CreateEmptyContext();

		foreach (var rule in RuleRegistry.Rules)
		{
			var result = await rule.EvaluateAsync(context, CancellationToken.None);
			result.Should().NotBeNull($"Rule {rule.RuleId} returned null");
			result.RuleId.Should().Be(rule.RuleId);
		}
	}

	[Fact]
	public async Task CI01_ShouldPass_WhenCiWorkflowExists()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "name: CI\non:\n  push:\n  pull_request:\n"
		});

		var rule = GetRule("CI-01");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI01_ShouldFail_WhenCiWorkflowMissing()
	{
		var context = CreateEmptyContext();

		var rule = GetRule("CI-01");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Remediation.Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task CPM01_ShouldPass_WhenCpmEnabled()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>"
		});

		var rule = GetRule("CPM-01");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task LIC01_ShouldPass_WhenMitLicenseExists()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["LICENSE"] = "MIT License\n\nCopyright (c) 2025 Panoramic Data Limited\n"
		});

		var rule = GetRule("LIC-01");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task SER01_ShouldFail_WhenNewtonsoftReferenced()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup><PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>"
		});

		var rule = GetRule("SER-01");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task VER01_ShouldPass_WhenVersionJsonMatchesDefaultPublishingRefSpec()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["version.json"] = """
				{
					"version": "1.0",
					"publicReleaseRefSpec": [
						"^refs/heads/main$",
						"^refs/tags/\\d+\\.\\d+\\.\\d+$"
					]
				}
				"""
		});

		var rule = GetRule("VER-01");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task VER01_ShouldPass_WhenVersionJsonMatchesOverriddenPublishingRefSpec()
	{
		var options = new RepoOptions
		{
			Publishing = new PublishingOptions
			{
				PublicReleaseRefSpec =
				[
					"^refs/heads/main$",
					"^refs/tags/v\\d+\\.\\d+\\.\\d+$"
				]
			}
		};

		var context = CreateContext(new Dictionary<string, string>
		{
			["version.json"] = """
				{
					"version": "1.0",
					"publicReleaseRefSpec": [
						"^refs/heads/main$",
						"^refs/tags/v\\d+\\.\\d+\\.\\d+$"
					]
				}
				"""
		}, options);

		var rule = GetRule("VER-01");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	private static IRule GetRule(string ruleId)
		=> RuleRegistry.Rules.Single(r => r.RuleId == ruleId);

	private static RepositoryContext CreateEmptyContext() => new()
	{
		FullName = "test-org/test-repo",
		Name = "test-repo",
		DefaultBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [],
		FileContents = new Dictionary<string, string>()
	};

	private static RepositoryContext CreateContext(Dictionary<string, string> files, RepoOptions? options = null) => new()
	{
		FullName = "test-org/test-repo",
		Name = "test-repo",
		DefaultBranch = "main",
		Options = options ?? new RepoOptions(),
		FilePaths = [.. files.Keys],
		FileContents = files
	};
}
