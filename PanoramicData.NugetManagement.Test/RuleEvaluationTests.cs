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
		result.Advisory.Should().NotBeNull();
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
	public async Task BLD03_ShouldIgnoreExcludedProjectFromRepositoryConfig()
	{
		var repoConfig = new NugetManagementRepositoryConfig
		{
			Projects = new Dictionary<string, NugetManagementProjectConfig>
			{
				["Fixtures/FailArmy/FailArmy.csproj"] = new() { Treatment = ProjectTreatment.Exclude }
			}
		};

		var context = CreateContext(new Dictionary<string, string>
		{
			["Fixtures/FailArmy/FailArmy.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		}, config: repoConfig);

		var rule = GetRule("BLD-03");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST01_ShouldTreatTestingIncludeProjectAsTestProject()
	{
		var repoConfig = new NugetManagementRepositoryConfig
		{
			Projects = new Dictionary<string, NugetManagementProjectConfig>
			{
				["Fixtures/FailArmy/FailArmy.csproj"] = new()
				{
					Treatment = ProjectTreatment.Exclude,
					TestingTreatment = ProjectTreatment.Include
				}
			}
		};

		var context = CreateContext(new Dictionary<string, string>
		{
			["Fixtures/FailArmy/FailArmy.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		}, config: repoConfig);

		var rule = GetRule("TST-01");
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
		result.Advisory.Should().NotBeNull();
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
	public async Task REPO06_ShouldPass_WhenDefaultAndCurrentBranchAreMain()
	{
		var context = CreateEmptyContext(defaultBranch: "main", currentBranch: "main");

		var rule = GetRule("REPO-06");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task REPO06_ShouldFail_WhenDefaultBranchIsMaster()
	{
		var context = CreateEmptyContext(defaultBranch: "master", currentBranch: "master");

		var rule = GetRule("REPO-06");
		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Advisory.Should().NotBeNull();
		result.Message.Should().Contain("master");
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

	// ── BLD-01 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task BLD01_ShouldPass_WhenTreatWarningsAsErrorsEnabled()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>"
		});

		var result = await GetRule("BLD-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task BLD01_ShouldFail_WhenTreatWarningsAsErrorsMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("BLD-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── BLD-02 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task BLD02_ShouldPass_WhenNullableEnabled()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>"
		});

		var result = await GetRule("BLD-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task BLD02_ShouldFail_WhenNullableMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("BLD-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── BLD-03 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task BLD03_ShouldPass_WhenImplicitUsingsEnabled()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject/MyProject.csproj"] = "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>"
		});

		var result = await GetRule("BLD-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task BLD03_ShouldFail_WhenImplicitUsingsMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject/MyProject.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("BLD-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CI-02 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CI02_ShouldPass_WhenTriggersOnPushAndPrToMain()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "on:\n  push:\n    branches: [main]\n  pull_request:\n    branches: [main]\n"
		});

		var result = await GetRule("CI-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI02_ShouldFail_WhenTriggersMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "on:\n  workflow_dispatch:\n"
		});

		var result = await GetRule("CI-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CI-03 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CI03_ShouldPass_WhenAllBuildStepsPresent()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "steps:\n- run: dotnet restore\n- run: dotnet build --configuration Release\n- run: dotnet pack\n"
		});

		var result = await GetRule("CI-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI03_ShouldFail_WhenBuildStepsMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "steps:\n- run: echo hello\n"
		});

		var result = await GetRule("CI-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CI-04 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CI04_ShouldPass_WhenFetchDepthZero()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "steps:\n- uses: actions/checkout@v4\n  with:\n    fetch-depth: 0\n"
		});

		var result = await GetRule("CI-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI04_ShouldFail_WhenFetchDepthNotSet()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "steps:\n- uses: actions/checkout@v4\n"
		});

		var result = await GetRule("CI-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CI-05 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CI05_ShouldPass_WhenLatestCheckoutUsed()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = $"steps:\n- uses: actions/checkout@{Standards.LatestActionsCheckoutVersion}\n"
		});

		var result = await GetRule("CI-05").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI05_ShouldFail_WhenOldCheckoutUsed()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "steps:\n- uses: actions/checkout@v2\n"
		});

		var result = await GetRule("CI-05").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CI-06 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CI06_ShouldPass_WhenLatestSetupDotnetUsed()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = $"- uses: actions/setup-dotnet@{Standards.LatestActionsSetupDotnetVersion}\n  with:\n    dotnet-version: {Standards.LatestDotNetVersionSpecifier}\n"
		});

		var result = await GetRule("CI-06").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI06_ShouldFail_WhenOldSetupDotnetUsed()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "- uses: actions/setup-dotnet@v1\n  with:\n    dotnet-version: 6.0.x\n"
		});

		var result = await GetRule("CI-06").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CI-07 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CI07_ShouldPass_WhenPublishPs1Exists()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Publish.ps1"] = "# publish script"
		});

		var result = await GetRule("CI-07").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI07_ShouldFail_WhenPublishPs1Missing()
	{
		var context = CreateEmptyContext();

		var result = await GetRule("CI-07").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CI-08 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CI08_ShouldPass_WhenAllRequiredSnippetsPresent()
	{
		var ci = string.Join("\n",
		[
			"on:",
			"  push:",
			"    tags: ['[0-9]*.[0-9]*.[0-9]*']",
			"jobs:",
			"  build:",
			"    steps:",
			"    - uses: actions/upload-artifact@v4",
			"  publish:",
			"    if: startsWith(github.ref, 'refs/tags/')",
			"    permissions:",
			"      id-token: write",
			"    steps:",
			"    - uses: NuGet/login@v1",
			"      with:",
			"        user: david_n_m_bond",
			"    - run: dotnet nuget push ./artifacts/*.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }}"
		]);

		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = ci
		});

		var result = await GetRule("CI-08").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI08_ShouldFail_WhenSnippetsMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "name: CI\non:\n  push:\n"
		});

		var result = await GetRule("CI-08").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task CI08_ShouldFail_WhenNuGetLoginUsesServerUrlInsteadOfNamedUser()
	{
		var ci = string.Join("\n",
		[
			"on:",
			"  push:",
			"    tags: ['[0-9]*.[0-9]*.[0-9]*']",
			"jobs:",
			"  build:",
			"    steps:",
			"    - uses: actions/upload-artifact@v4",
			"  publish:",
			"    if: startsWith(github.ref, 'refs/tags/')",
			"    permissions:",
			"      id-token: write",
			"    steps:",
			"    - uses: NuGet/login@v1",
			"      with:",
			"        nuget-server-url: https://api.nuget.org/v3/index.json",
			"    - run: dotnet nuget push ./artifacts/*.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }}"
		]);

		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = ci
		});

		var result = await GetRule("CI-08").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CI-09 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CI09_ShouldPass_WhenPublishPs1MatchesStandard()
	{
		var ps1 = """
			$status = git status --porcelain
			$branch = git rev-parse --abbrev-ref HEAD
			git fetch origin main --quiet
			$json = nbgv get-version -f json
			$exists = git tag -l $version
			git tag $version
			git push origin $version
			""";

		var context = CreateContext(new Dictionary<string, string>
		{
			["Publish.ps1"] = ps1
		});

		var result = await GetRule("CI-09").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI09_ShouldFail_WhenPublishPs1DoesNotMatchStandard()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Publish.ps1"] = "dotnet nuget push *.nupkg"
		});

		var result = await GetRule("CI-09").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CI-10 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CI10_ShouldPass_WhenNugetKeyNotCommitted()
	{
		var context = CreateEmptyContext();

		var result = await GetRule("CI-10").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI10_ShouldFail_WhenNugetKeyCommitted()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["nuget-key.txt"] = "oy2abc123..."
		});

		var result = await GetRule("CI-10").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CPM-02 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task CPM02_ShouldPass_WhenNoVersionInPackageReference()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject/MyProject.csproj"] = "<Project><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" /></ItemGroup></Project>"
		});

		var result = await GetRule("CPM-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CPM02_ShouldFail_WhenVersionInPackageReference()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject/MyProject.csproj"] = "<Project><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>"
		});

		var result = await GetRule("CPM-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CQ-01 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CQ01_ShouldPass_WhenEditorConfigExistsWithRoot()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".editorconfig"] = "root = true\n[*.cs]\nindent_style = tab\n"
		});

		var result = await GetRule("CQ-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CQ01_ShouldFail_WhenEditorConfigMissing()
	{
		var context = CreateEmptyContext();

		var result = await GetRule("CQ-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CQ-02 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CQ02_ShouldPass_WhenFileScopedNamespacesEnforced()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".editorconfig"] = "root = true\n[*.cs]\ncsharp_style_namespace_declarations = file_scoped:error\n"
		});

		var result = await GetRule("CQ-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CQ02_ShouldFail_WhenFileScopedNamespacesNotEnforced()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".editorconfig"] = "root = true\n[*.cs]\nindent_style = tab\n"
		});

		var result = await GetRule("CQ-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── CQ-03 ───────────────────────────────────────────────────────────

	[Fact]
	public async Task CQ03_ShouldPass_WhenCodacyYmlExists()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".codacy.yml"] = "engines:\n  eslint:\n    enabled: true\n"
		});

		var result = await GetRule("CQ-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CQ03_ShouldFail_WhenCodacyNotConfigured()
	{
		var context = CreateEmptyContext();

		var result = await GetRule("CQ-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task CQ03_ShouldPass_WhenCodacyBadgeInReadme()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["README.md"] = "# My Package\n[![Codacy Badge](https://app.codacy.com/project/badge/Grade/abc123)](https://app.codacy.com/gh/org/repo/dashboard)"
		});

		var result = await GetRule("CQ-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	// ── COM-01 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task COM01_ShouldPass_WhenSecurityMdExists()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["SECURITY.md"] = "# Security Policy"
		});

		var result = await GetRule("COM-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task COM01_ShouldFail_WhenSecurityMdMissing()
	{
		var context = CreateEmptyContext();

		var result = await GetRule("COM-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── COM-02 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task COM02_ShouldPass_WhenContributingMdExists()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["CONTRIBUTING.md"] = "# Contributing"
		});

		var result = await GetRule("COM-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task COM02_ShouldFail_WhenContributingMdMissing()
	{
		var context = CreateEmptyContext();

		var result = await GetRule("COM-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── COM-03 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task COM03_ShouldPass_WhenDependabotConfigured()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/dependabot.yml"] = "version: 2\nupdates:\n"
		});

		var result = await GetRule("COM-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task COM03_ShouldFail_WhenNoDependencyAutomation()
	{
		var context = CreateEmptyContext();

		var result = await GetRule("COM-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── COM-04 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task COM04_ShouldPass_WhenCodeQlWorkflowExists()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/codeql.yml"] = "name: CodeQL\non:\n  push:\n"
		});

		var result = await GetRule("COM-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task COM04_ShouldFail_WhenNoCodeQlWorkflow()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".github/workflows/ci.yml"] = "name: CI\non:\n  push:\n"
		});

		var result = await GetRule("COM-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── DOC-01 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task DOC01_ShouldPass_WhenGenerateDocumentationFileEnabled()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup><GenerateDocumentationFile>true</GenerateDocumentationFile></PropertyGroup></Project>"
		});

		var result = await GetRule("DOC-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task DOC01_ShouldFail_WhenGenerateDocumentationFileDisabled()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup></PropertyGroup></Project>",
			["MyProject/MyProject.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("DOC-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── HTTP-01 ─────────────────────────────────────────────────────────

	[Fact]
	public async Task HTTP01_ShouldPass_WhenRefitReferenced()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup><PackageVersion Include=\"Refit\" Version=\"7.0.0\" /></ItemGroup></Project>"
		});

		var result = await GetRule("HTTP-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task HTTP01_ShouldFail_WhenRefitMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup></ItemGroup></Project>",
			["MyProject/MyProject.csproj"] = "<Project><ItemGroup></ItemGroup></Project>"
		});

		var result = await GetRule("HTTP-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── LIC-02 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task LIC02_ShouldPass_WhenPackageLicenseExpressionMatches()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup><PackageLicenseExpression>MIT</PackageLicenseExpression></PropertyGroup></Project>"
		});

		var result = await GetRule("LIC-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task LIC02_ShouldFail_WhenPackageLicenseExpressionMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("LIC-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── LIC-03 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task LIC03_ShouldPass_WhenCopyrightMessagePresent()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup><Copyright>Copyright © 2025 Panoramic Data Limited</Copyright></PropertyGroup></Project>"
		});

		var result = await GetRule("LIC-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task LIC03_ShouldFail_WhenCopyrightMessageMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("LIC-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── PKG-01 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task PKG01_ShouldPass_WhenSnupkgEnabled()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup><IncludeSymbols>true</IncludeSymbols><SymbolPackageFormat>snupkg</SymbolPackageFormat></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task PKG01_ShouldFail_WhenSnupkgDisabled()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── PKG-02 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task PKG02_ShouldPass_WhenGeneratePackageOnBuildEnabled()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup><GeneratePackageOnBuild>true</GeneratePackageOnBuild></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task PKG02_ShouldFail_WhenGeneratePackageOnBuildMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── PKG-03 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task PKG03_ShouldPass_WhenPackageReadmeFileSet()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup><PackageReadmeFile>README.md</PackageReadmeFile></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task PKG03_ShouldFail_WhenPackageReadmeFileMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── PKG-04 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task PKG04_ShouldPass_WhenNuGetAuditModeAll()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup><NuGetAuditMode>All</NuGetAuditMode></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task PKG04_ShouldFail_WhenNuGetAuditModeMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── PKG-05 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task PKG05_ShouldFail_WhenBuildLevelUpdatesAvailable()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup><PackageVersion Include=\"Example.Package\" Version=\"1.2.3\" /></ItemGroup></Project>"
		});

		var rule = new NuGetBuildLevelUpdatesRule((packageId, currentVersion, _) =>
			Task.FromResult<PackageVersionStatus?>(new(packageId, currentVersion, "1.2.4", PackageUpdateLevel.Build)));

		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Warning);
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Data["remediation_type"].Should().Be("update_package_versions");
	}

	// ── PKG-06 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task PKG06_ShouldFail_WhenMinorLevelUpdatesAvailable()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup><PackageVersion Include=\"Example.Package\" Version=\"1.2.3\" /></ItemGroup></Project>"
		});

		var rule = new NuGetMinorLevelUpdatesRule((packageId, currentVersion, _) =>
			Task.FromResult<PackageVersionStatus?>(new(packageId, currentVersion, "1.3.0", PackageUpdateLevel.Minor)));

		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Error);
	}

	// ── PKG-07 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task PKG07_ShouldFail_WhenMajorLevelUpdatesAvailable()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup><PackageVersion Include=\"Example.Package\" Version=\"1.2.3\" /></ItemGroup></Project>"
		});

		var rule = new NuGetMajorLevelUpdatesRule((packageId, currentVersion, _) =>
			Task.FromResult<PackageVersionStatus?>(new(packageId, currentVersion, "2.0.0", PackageUpdateLevel.Major)));

		var result = await rule.EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Critical);
	}

	// ── PKG-09 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task PKG09_ShouldPass_WhenAncillaryProjectsAreExplicitlyNonPackable()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup><PackageId>test-repo</PackageId></PropertyGroup></Project>",
			["Generator/Generator.csproj"] = "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>",
			["Tooling/Tooling.csproj"] = "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-09").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task PKG09_ShouldFail_WhenAncillaryProjectIsNotExplicitlyNonPackable()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup><PackageId>test-repo</PackageId></PropertyGroup></Project>",
			["Generator/Generator.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("PKG-09").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── META-01 ─────────────────────────────────────────────────────────

	[Fact]
	public async Task META01_ShouldPass_WhenPackageIdSet()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject/MyProject.csproj"] = "<Project><PropertyGroup><PackageId>MyProject</PackageId></PropertyGroup></Project>"
		});

		var result = await GetRule("META-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task META01_ShouldFail_WhenPackageIdMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("META-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── META-02 ─────────────────────────────────────────────────────────

	[Fact]
	public async Task META02_ShouldPass_WhenRepositoryUrlSet()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup><RepositoryUrl>https://github.com/org/repo</RepositoryUrl></PropertyGroup></Project>"
		});

		var result = await GetRule("META-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task META02_ShouldFail_WhenRepositoryUrlMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("META-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── META-03 ─────────────────────────────────────────────────────────

	[Fact]
	public async Task META03_ShouldPass_WhenAuthorsAndCompanySet()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup><Authors>Panoramic Data Limited</Authors><Company>Panoramic Data Limited</Company></PropertyGroup></Project>"
		});

		var result = await GetRule("META-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task META03_ShouldFail_WhenAuthorsOrCompanyMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("META-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── META-04 ─────────────────────────────────────────────────────────

	[Fact]
	public async Task META04_ShouldPass_WhenPackageProjectUrlAndIconSet()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup><PackageProjectUrl>https://github.com/org/repo</PackageProjectUrl><PackageIcon>Logo.png</PackageIcon></PropertyGroup></Project>"
		});

		var result = await GetRule("META-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task META04_ShouldFail_WhenPackageProjectUrlOrIconMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("META-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task META04_ShouldPass_WhenNonPackableProjectMissingMetadata()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup><PackageProjectUrl>https://github.com/org/repo</PackageProjectUrl><PackageIcon>Logo.png</PackageIcon></PropertyGroup></Project>",
			["ExampleApp/ExampleApp.csproj"] = "<Project><PropertyGroup><OutputType>Exe</OutputType><IsPackable>false</IsPackable></PropertyGroup></Project>"
		});

		var result = await GetRule("META-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task META01_ShouldPass_WhenNonPackableProjectMissingPackageId()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["test-repo/test-repo.csproj"] = "<Project><PropertyGroup><PackageId>test-repo</PackageId></PropertyGroup></Project>",
			["ExampleApp/ExampleApp.csproj"] = "<Project><PropertyGroup><OutputType>Exe</OutputType><IsPackable>false</IsPackable></PropertyGroup></Project>"
		});

		var result = await GetRule("META-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	// ── README-01 ───────────────────────────────────────────────────────

	[Fact]
	public async Task README01_ShouldPass_WhenReadmeIsComprehensive()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["README.md"] = new string('x', 250)
		});

		var result = await GetRule("README-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task README01_ShouldFail_WhenReadmeTooShort()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["README.md"] = "# Hello"
		});

		var result = await GetRule("README-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── README-02 ───────────────────────────────────────────────────────

	[Fact]
	public async Task README02_ShouldPass_WhenCodacyBadgePresent()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["README.md"] = "[![Codacy Badge](https://app.codacy.com/project/badge)](https://app.codacy.com)"
		});

		var result = await GetRule("README-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task README02_ShouldFail_WhenCodacyBadgeMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["README.md"] = "# My Project\nSome readme text"
		});

		var result = await GetRule("README-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── README-03 ───────────────────────────────────────────────────────

	[Fact]
	public async Task README03_ShouldPass_WhenNuGetBadgePresent()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["README.md"] = "[![NuGet](https://img.shields.io/nuget/v/MyPackage)](https://www.nuget.org/packages/MyPackage)"
		});

		var result = await GetRule("README-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task README03_ShouldFail_WhenNuGetBadgeMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["README.md"] = "# My Project"
		});

		var result = await GetRule("README-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── README-04 ───────────────────────────────────────────────────────

	[Fact]
	public async Task README04_ShouldPass_WhenLicenseBadgePresent()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["README.md"] = "[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)"
		});

		var result = await GetRule("README-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task README04_ShouldFail_WhenLicenseBadgeMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["README.md"] = "# My Project"
		});

		var result = await GetRule("README-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── REPO-01 ─────────────────────────────────────────────────────────

	[Fact]
	public async Task REPO01_ShouldPass_WhenGitignoreHasEssentials()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".gitignore"] = "[Bb]in/\n[Oo]bj/\n.vs/\n"
		});

		var result = await GetRule("REPO-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task REPO01_ShouldFail_WhenGitignoreMissing()
	{
		var context = CreateEmptyContext();

		var result = await GetRule("REPO-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── REPO-02 ─────────────────────────────────────────────────────────

	[Fact]
	public async Task REPO02_ShouldPass_WhenNugetKeyGitignored()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".gitignore"] = "nuget-key.txt\n"
		});

		var result = await GetRule("REPO-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task REPO02_ShouldFail_WhenNugetKeyNotGitignored()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[".gitignore"] = "[Bb]in/\n"
		});

		var result = await GetRule("REPO-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── REPO-03 ─────────────────────────────────────────────────────────

	[Fact]
	public async Task REPO03_ShouldPass_WhenNeutralResourcesLanguageSet()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject/MyProject.csproj"] = "<Project><PropertyGroup><NeutralResourcesLanguage>en</NeutralResourcesLanguage></PropertyGroup></Project>"
		});

		var result = await GetRule("REPO-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task REPO03_ShouldFail_WhenNeutralResourcesLanguageMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject/MyProject.csproj"] = "<Project><PropertyGroup></PropertyGroup></Project>"
		});

		var result = await GetRule("REPO-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── TFM-01 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task TFM01_ShouldPass_WhenLatestFrameworkTargeted()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject/MyProject.csproj"] = $"<Project><PropertyGroup><TargetFramework>{Standards.LatestTargetFramework}</TargetFramework></PropertyGroup></Project>"
		});

		var result = await GetRule("TFM-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TFM01_ShouldFail_WhenOutdatedFrameworkTargeted()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject/MyProject.csproj"] = "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"
		});

		var result = await GetRule("TFM-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── TST-01 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task TST01_ShouldPass_WhenTestProjectExists()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject.Test/MyProject.Test.csproj"] = "<Project/>"
		});

		var result = await GetRule("TST-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST01_ShouldFail_WhenNoTestProject()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject/MyProject.csproj"] = "<Project/>"
		});

		var result = await GetRule("TST-01").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── TST-02 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task TST02_ShouldPass_WhenXunitV3Referenced()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup><PackageVersion Include=\"xunit.v3\" Version=\"1.0.0\" /></ItemGroup></Project>"
		});

		var result = await GetRule("TST-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST02_ShouldFail_WhenXunitV3NotReferenced()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup><PackageVersion Include=\"xunit\" Version=\"2.4.2\" /></ItemGroup></Project>"
		});

		var result = await GetRule("TST-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── TST-03 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task TST03_ShouldPass_WhenTestSdkReferenced()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup><PackageVersion Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" /></ItemGroup></Project>"
		});

		var result = await GetRule("TST-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST03_ShouldFail_WhenTestSdkMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup></ItemGroup></Project>"
		});

		var result = await GetRule("TST-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── TST-04 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task TST04_ShouldFail_WhenCoverletCollectorOnlyPinnedInDirectoryPackagesPropsWithCpm()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"coverlet.collector\" Version=\"6.0.0\" /></ItemGroup></Project>",
			["MyProject.Test/MyProject.Test.csproj"] = "<Project><ItemGroup></ItemGroup></Project>"
		});

		var result = await GetRule("TST-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Data["remediation_type"].Should().Be("ensure_coverlet_collector_setup");
	}

	[Fact]
	public async Task TST04_ShouldPass_WhenCoverletCollectorPinnedAndReferencedWithCpm()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"coverlet.collector\" Version=\"6.0.0\" /></ItemGroup></Project>",
			["MyProject.Test/MyProject.Test.csproj"] = "<Project><ItemGroup><PackageReference Include=\"coverlet.collector\" /></ItemGroup></Project>"
		});

		var result = await GetRule("TST-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST04_ShouldPass_WhenCoverletCollectorReferencedInTestProjectWithoutCpm()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["MyProject.Test/MyProject.Test.csproj"] = "<Project><ItemGroup><PackageReference Include=\"coverlet.collector\" Version=\"6.0.0\" /></ItemGroup></Project>"
		});

		var result = await GetRule("TST-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST04_ShouldFail_WhenCoverletCollectorMissing()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup></ItemGroup></Project>"
		});

		var result = await GetRule("TST-04").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Data["remediation_type"].Should().Be("ensure_coverlet_collector_setup");
	}

	// ── VER-02 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task VER02_ShouldPass_WhenNerdbankReferenced()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup><PackageVersion Include=\"Nerdbank.GitVersioning\" Version=\"3.6.0\" /></ItemGroup></Project>"
		});

		var result = await GetRule("VER-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task VER02_ShouldFail_WhenNerdbankNotReferenced()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Directory.Packages.props"] = "<Project><ItemGroup></ItemGroup></Project>",
			["MyProject/MyProject.csproj"] = "<Project/>"
		});

		var result = await GetRule("VER-02").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	// ── VER-03 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task VER03_ShouldPass_WhenGlobalJsonHasCorrectSdk()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["global.json"] = $"{{\"sdk\":{{\"version\":\"{Standards.LatestDotNetSdkVersion}\",\"rollForward\":\"latestFeature\"}}}}"
		});

		var result = await GetRule("VER-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task VER03_ShouldFail_WhenGlobalJsonMissing()
	{
		var context = CreateEmptyContext();

		var result = await GetRule("VER-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task VER03_ShouldProvideAutoFix_WhenGlobalJsonHasWrongSdk()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["global.json"] = "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestFeature\"}}"
		});

		var result = await GetRule("VER-03").EvaluateAsync(context, CancellationToken.None);
		result.Passed.Should().BeFalse();
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Data["remediation_type"].Should().Be("replace_file_content");
	}

	private static IRule GetRule(string ruleId)
		=> RuleRegistry.Rules.Single(r => r.RuleId == ruleId);

	private static RepositoryContext CreateEmptyContext(string defaultBranch = "main", string? currentBranch = "main") => new()
	{
		FullName = "test-org/test-repo",
		Name = "test-repo",
		DefaultBranch = defaultBranch,
		CurrentBranch = currentBranch,
		Options = new RepoOptions(),
		FilePaths = [],
		FileContents = new Dictionary<string, string>(),
		RepositoryConfig = null
	};

	private static RepositoryContext CreateContext(
		Dictionary<string, string> files,
		RepoOptions? options = null,
		string defaultBranch = "main",
		string? currentBranch = "main",
		NugetManagementRepositoryConfig? config = null) => new()
	{
		FullName = "test-org/test-repo",
		Name = "test-repo",
		DefaultBranch = defaultBranch,
		CurrentBranch = currentBranch,
		Options = options ?? new RepoOptions(),
		FilePaths = [.. files.Keys],
		FileContents = files,
		RepositoryConfig = config
	};
}
