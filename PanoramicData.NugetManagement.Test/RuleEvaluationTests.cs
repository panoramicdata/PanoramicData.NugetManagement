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

	[Fact]
	public async Task REPO04_ShouldPass_WhenSlnxExists()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject.slnx"] = "<Solution><Project Path=\"MyProject/MyProject.csproj\" /></Solution>"
		});

		var rule = GetRule("REPO-04");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task REPO04_ShouldFail_WhenSlnxMissing()
	{
		var context = CreateEmptyContext();

		var rule = GetRule("REPO-04");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Remediation.Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task REPO05_ShouldPass_WhenSolutionItemsContainsAllStandardFiles()
	{
		var slnxContent = """
			<Solution>
			  <Folder Name="/Solution Items/">
				<File Path=".editorconfig" />
				<File Path=".gitignore" />
				<File Path="Directory.Build.props" />
				<File Path="Directory.Packages.props" />
				<File Path="global.json" />
				<File Path="LICENSE" />
				<File Path="README.md" />
				<File Path="SECURITY.md" />
				<File Path="CONTRIBUTING.md" />
				<File Path="version.json" />
			  </Folder>
			</Solution>
			""";

		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject.slnx"] = slnxContent,
			[".editorconfig"] = "root = true",
			[".gitignore"] = "[Bb]in/",
			["Directory.Build.props"] = "<Project/>",
			["Directory.Packages.props"] = "<Project/>",
			["global.json"] = "{}",
			["LICENSE"] = "MIT",
			["README.md"] = "# Readme",
			["SECURITY.md"] = "# Security",
			["CONTRIBUTING.md"] = "# Contributing",
			["version.json"] = "{}"
		});

		var rule = GetRule("REPO-05");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task REPO05_ShouldFail_WhenSolutionItemsMissing()
	{
		var slnxContent = """
			<Solution>
			  <Project Path="MyProject/MyProject.csproj" />
			</Solution>
			""";

		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject.slnx"] = slnxContent,
			[".editorconfig"] = "root = true"
		});

		var rule = GetRule("REPO-05");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task REPO05_ShouldFail_WhenSolutionItemsMissingFiles()
	{
		var slnxContent = """
			<Solution>
			  <Folder Name="/Solution Items/">
				<File Path=".editorconfig" />
			  </Folder>
			</Solution>
			""";

		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject.slnx"] = slnxContent,
			[".editorconfig"] = "root = true",
			["README.md"] = "# Readme",
			["LICENSE"] = "MIT"
		});

		var rule = GetRule("REPO-05");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("README.md");
		result.Message.Should().Contain("LICENSE");
	}

	[Fact]
	public async Task CQ04_ShouldPass_WhenTabIndentationEnforced()
	{
		var editorconfig = """
			root = true

			[*]
			indent_style = tab
			indent_size = 4
			""";

		var context = CreateContext(new Dictionary<string, string>
		{
			[".editorconfig"] = editorconfig
		});

		var rule = GetRule("CQ-04");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CQ04_ShouldFail_WhenSpaceIndentationUsed()
	{
		var editorconfig = """
			root = true

			[*]
			indent_style = space
			indent_size = 4
			""";

		var context = CreateContext(new Dictionary<string, string>
		{
			[".editorconfig"] = editorconfig
		});

		var rule = GetRule("CQ-04");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task CQ04_ShouldFail_WhenCsSectionOverridesToSpaces()
	{
		var editorconfig = """
			root = true

			[*]
			indent_style = tab

			[*.cs]
			indent_style = space
			""";

		var context = CreateContext(new Dictionary<string, string>
		{
			[".editorconfig"] = editorconfig
		});

		var rule = GetRule("CQ-04");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("[*.cs]");
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
