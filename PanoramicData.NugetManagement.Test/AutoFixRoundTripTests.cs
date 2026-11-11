using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Remediations;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Round-trip tests for the rules that used to be AI-only despite the fix being mechanical: assess a
/// repository on disk, apply the remediation the rule emitted, then re-assess and require a pass.
/// Asserting only that a payload exists would not catch a payload that edits nothing, which is what
/// TST-05 shipped — an <c>add_file</c> type no remediation knew how to apply.
/// </summary>
[Collection(ActionVersionCatalogCollection.Name)]
public class AutoFixRoundTripTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	// An in-memory catalog (null path) so assessing these fixtures never writes action-versions.json.
	static AutoFixRoundTripTests() => ActionVersionCatalog.Default = new ActionVersionCatalog(null);

	private const string _ciPath = ".github/workflows/ci.yml";

	private readonly string _root = Directory.CreateTempSubdirectory("nugetmgmt-autofix-").FullName;

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A locked temp file must not fail the test that produced it.
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task CI04_FixesACheckoutStepThatHasNoWithBlock()
	{
		var workflow = """
			jobs:
			  build:
			    steps:
			      - uses: actions/checkout@v7
			      - uses: actions/setup-dotnet@v6
			""";

		var fixedWorkflow = await FixAsync("CI-04", workflow);

		fixedWorkflow.Should().Contain("      - uses: actions/checkout@v7\n        with:\n          fetch-depth: 0\n");
		fixedWorkflow.Should().Contain("- uses: actions/setup-dotnet@v6", "the next step must survive intact");
	}

	[Fact]
	public async Task CI04_AddsFetchDepthToACheckoutStepThatAlreadyHasOtherInputs()
	{
		var workflow = """
			jobs:
			  build:
			    steps:
			      - name: Checkout
			        uses: actions/checkout@v7
			        with:
			          submodules: true
			""";

		var fixedWorkflow = await FixAsync("CI-04", workflow);

		fixedWorkflow.Should().Contain("          fetch-depth: 0");
		fixedWorkflow.Should().Contain("          submodules: true", "the existing input must not be replaced");
	}

	[Fact]
	public async Task CI04_ReplacesAShallowFetchDepthRatherThanAddingASecondOne()
	{
		var workflow = """
			jobs:
			  build:
			    steps:
			      - uses: actions/checkout@v7
			        with:
			          fetch-depth: 1
			""";

		var fixedWorkflow = await FixAsync("CI-04", workflow);

		fixedWorkflow.Should().Contain("fetch-depth: 0");
		fixedWorkflow.Should().NotContain("fetch-depth: 1");
	}

	[Fact]
	public async Task CI04_IsAiOnlyWhenThereIsNoCheckoutStepToChange()
	{
		var context = Context("jobs:\n  build:\n    steps:\n      - run: dotnet build\n");
		var result = await Rule("CI-04").EvaluateAsync(context, TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		new RemediationRegistry().CanRemediate(result).Should()
			.BeFalse("where the checkout step belongs in the job is a judgement, not a rewrite");
	}

	[Fact]
	public async Task CI05_BumpsAnOutdatedCheckoutVersion()
	{
		var workflow = """
			jobs:
			  build:
			    steps:
			      - uses: actions/checkout@v4
			        with:
			          fetch-depth: 0
			""";

		var fixedWorkflow = await FixAsync("CI-05", workflow);

		fixedWorkflow.Should().NotContain("actions/checkout@v4");
	}

	[Fact]
	public async Task CI05_IsAiOnlyWhenTheWorkflowHasNoCheckoutStep()
	{
		var context = Context("jobs:\n  build:\n    steps:\n      - run: dotnet build\n");
		var result = await Rule("CI-05").EvaluateAsync(context, TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		new RemediationRegistry().CanRemediate(result).Should().BeFalse("there is no version to bump");
	}

	[Fact]
	public async Task CI06_BumpsBothTheActionVersionAndTheSdkVersion()
	{
		var workflow = """
			jobs:
			  build:
			    steps:
			      - uses: actions/setup-dotnet@v3
			        with:
			          dotnet-version: 8.0.x
			""";

		var fixedWorkflow = await FixAsync("CI-06", workflow);

		fixedWorkflow.Should().NotContain("actions/setup-dotnet@v3");
		fixedWorkflow.Should().NotContain("8.0.x");
		fixedWorkflow.Should().Contain(Standards.LatestDotNetVersionSpecifier);
	}

	[Theory]
	[InlineData("\n", "v6", "10.0.x")]
	[InlineData("\r\n", "v6", "10.0.x")]
	[InlineData("\r\n", "v6", "'10.0.x'")]
	[InlineData("\n", "v5", "10.0.x")]
	public async Task CI06_MovesEveryDotnetVersionWhateverTheLineEndings(string eol, string action, string version)
	{
		var workflow = string.Join(eol,
		[
			"jobs:",
			"  build:",
			"    steps:",
			$"    - uses: actions/setup-dotnet@{action}",
			"      with:",
			$"        dotnet-version: {version}",
			"  publish:",
			"    steps:",
			$"    - uses: actions/setup-dotnet@{action}",
			"      with:",
			$"        dotnet-version: {version}",
			string.Empty
		]);

		var fixedWorkflow = await FixAsync("CI-06", workflow);

		fixedWorkflow.Should().NotContain("10.0.x");
		fixedWorkflow.Should().Contain(Standards.LatestDotNetVersionSpecifier);
	}

	[Fact]
	public async Task DotNetUpgrade_MovesAProjectGlobalJsonAndCiInOneFix_WhateverTheLineEndings()
	{
		const string csprojPath = "Src/Widget/Widget.csproj";
		var csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\t<PropertyGroup>\r\n\t\t<TargetFramework>net10.0</TargetFramework>\r\n\t</PropertyGroup>\r\n\t<ItemGroup>\r\n\t\t<PackageReference Include=\"Microsoft.Extensions.Logging.Abstractions\" Version=\"11.0.0\" />\r\n\t\t<PackageReference Include=\"Refit\" Version=\"16.4.0\" />\r\n\t</ItemGroup>\r\n</Project>\r\n";
		var globalJson = "{\r\n\t\"sdk\": {\r\n\t\t\"version\": \"10.0.100\",\r\n\t\t\"rollForward\": \"latestMinor\"\r\n\t}\r\n}\r\n";
		var workflow = "jobs:\r\n  build:\r\n    steps:\r\n    - uses: actions/setup-dotnet@v6\r\n      with:\r\n        dotnet-version: 10.0.x\r\n";

		var files = new Dictionary<string, string>
		{
			[csprojPath] = csproj,
			["global.json"] = globalJson,
			[_ciPath] = workflow
		};

		foreach (var (path, content) in files)
		{
			WriteFile(path, content);
		}

		RepositoryContext FromDisk() => new()
		{
			FullName = "panoramicdata/Sample",
			Name = "Sample",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files.Keys.ToDictionary(path => path, path => File.ReadAllText(Path.Combine(_root, path)))
		};

		// PKG-15 only fails once the retarget has landed, so the upgrade is passes of assess-then-apply over the
		// uncommitted working tree, the way DashboardService.ApplyRemediationsAsync follows on.
		var rules = new[] { "TFM-01", "VER-03", "CI-06", "PKG-15" };
		var registry = new RemediationRegistry();
		var passes = 0;
		for (; passes < 3; passes++)
		{
			var context = FromDisk();
			var appliedThisPass = 0;
			foreach (var ruleId in rules)
			{
				var result = await Rule(ruleId).EvaluateAsync(context, TestContext.Current.CancellationToken);
				if (result.Passed)
				{
					continue;
				}

				registry.CanRemediate(result).Should().BeTrue($"{ruleId} must be fixable without the AI");
				var applied = new List<string>();
				registry.Get(ruleId)!.Apply(_root, result, applied, Output.WriteLine);
				applied.Should().NotBeEmpty($"{ruleId} must change something");
				appliedThisPass += applied.Count;
			}

			if (appliedThisPass == 0)
			{
				break;
			}
		}

		passes.Should().BeLessThan(3, "the upgrade must settle, not keep finding work");

		var fixedContext = FromDisk();
		foreach (var ruleId in rules)
		{
			var result = await Rule(ruleId).EvaluateAsync(fixedContext, TestContext.Current.CancellationToken);
			result.Passed.Should().BeTrue($"{ruleId} should pass once the upgrade fix has run: {result.Message}");
		}

		fixedContext.FileContents![csprojPath].Should().NotContain("Logging.Abstractions").And.Contain("Refit");
	}

	[Fact]
	public async Task TFM01_MovesATargetFrameworksListInsteadOfAddingAnIgnoredSingularProperty()
	{
		// MSBuild ignores a singular TargetFramework beside TargetFrameworks, so adding one left these projects
		// on the old framework with a restore that had no target for the new one (NETSDK1005).
		var previous = "net" + (int.Parse(Standards.LatestTargetFramework[3..Standards.LatestTargetFramework.IndexOf('.', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture) - 1) + ".0";
		string Project(string body) => $"<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\t<PropertyGroup>\r\n{body}\t</PropertyGroup>\r\n</Project>\r\n";

		var files = new Dictionary<string, string>
		{
			["Single/Single.csproj"] = Project($"\t\t<TargetFrameworks>{previous}</TargetFrameworks>\r\n"),
			["Duplicated/Duplicated.csproj"] = Project($"\t\t<TargetFrameworks>{previous};{previous}</TargetFrameworks>\r\n"),
			["Mixed/Mixed.csproj"] = Project($"\t\t<TargetFrameworks>netstandard2.0;{previous}</TargetFrameworks>\r\n"),
			["Both/Both.csproj"] = Project($"\t\t<TargetFrameworks>{previous}</TargetFrameworks>\r\n\t\t<TargetFramework>{previous}</TargetFramework>\r\n"),
			["Plain/Plain.csproj"] = Project($"\t\t<TargetFramework>{previous}</TargetFramework>\r\n")
		};

		foreach (var (path, content) in files)
		{
			WriteFile(path, content);
		}

		RepositoryContext FromDisk() => new()
		{
			FullName = "panoramicdata/Sample",
			Name = "Sample",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files.Keys.ToDictionary(path => path, path => File.ReadAllText(Path.Combine(_root, path)))
		};

		var result = await Rule("TFM-01").EvaluateAsync(FromDisk(), TestContext.Current.CancellationToken);
		result.Passed.Should().BeFalse();

		Apply(result);

		var after = FromDisk().FileContents!;
		var latest = Standards.LatestTargetFramework;
		after["Single/Single.csproj"].Should().Contain($"<TargetFrameworks>{latest}</TargetFrameworks>").And.NotContain("<TargetFramework>");
		after["Duplicated/Duplicated.csproj"].Should().Contain($"<TargetFrameworks>{latest}</TargetFrameworks>");
		after["Mixed/Mixed.csproj"].Should().Contain($"<TargetFrameworks>netstandard2.0;{latest}</TargetFrameworks>");
		after["Both/Both.csproj"].Should().Contain($"<TargetFrameworks>{latest}</TargetFrameworks>").And.Contain($"<TargetFramework>{latest}</TargetFramework>");
		after["Plain/Plain.csproj"].Should().Contain($"<TargetFramework>{latest}</TargetFramework>");

		var reassessed = await Rule("TFM-01").EvaluateAsync(FromDisk(), TestContext.Current.CancellationToken);
		reassessed.Passed.Should().BeTrue(reassessed.Message);
	}

	[Fact]
	public async Task DOC01_DocumentsOnlyTheNonTestProjectsNotTheWholeRepository()
	{
		// Directory.Build.props reaches test projects too, whose undocumented members then fail the build
		// under TreatWarningsAsErrors (CS1591), so the fix must name the projects instead.
		var files = new Dictionary<string, string>
		{
			["Directory.Build.props"] = "<Project>\r\n\t<PropertyGroup>\r\n\t\t<TreatWarningsAsErrors>true</TreatWarningsAsErrors>\r\n\t</PropertyGroup>\r\n</Project>\r\n",
			["Widget/Widget.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\t<PropertyGroup>\r\n\t\t<TargetFramework>net11.0</TargetFramework>\r\n\t</PropertyGroup>\r\n</Project>\r\n",
			["Widget.Test/Widget.Test.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\t<PropertyGroup>\r\n\t\t<TargetFramework>net11.0</TargetFramework>\r\n\t</PropertyGroup>\r\n\t<ItemGroup>\r\n\t\t<PackageReference Include=\"xunit.v3\" />\r\n\t</ItemGroup>\r\n</Project>\r\n"
		};

		foreach (var (path, content) in files)
		{
			WriteFile(path, content);
		}

		RepositoryContext FromDisk() => new()
		{
			FullName = "panoramicdata/Sample",
			Name = "Sample",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files.Keys.ToDictionary(path => path, path => File.ReadAllText(Path.Combine(_root, path)))
		};

		var result = await Rule("DOC-01").EvaluateAsync(FromDisk(), TestContext.Current.CancellationToken);
		result.Passed.Should().BeFalse();

		Apply(result);

		var after = FromDisk().FileContents!;
		after["Widget/Widget.csproj"].Should().Contain("GenerateDocumentationFile");
		after["Widget.Test/Widget.Test.csproj"].Should().NotContain("GenerateDocumentationFile", "tests are not documented");
		after["Directory.Build.props"].Should().NotContain("GenerateDocumentationFile", "that file reaches the test projects");

		var reassessed = await Rule("DOC-01").EvaluateAsync(FromDisk(), TestContext.Current.CancellationToken);
		reassessed.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task CI06_IsAiOnlyWhenDotnetVersionIsAList()
	{
		// A multi-version block is there for a reason this rule cannot see, so rewriting one line of
		// it would be a guess. The AI gets to read why the other entries exist.
		var workflow = "jobs:\n  build:\n    steps:\n      - uses: actions/setup-dotnet@v6\n"
			+ "        with:\n          dotnet-version: |\n            8.0.x\n            9.0.x\n";

		var result = await Rule("CI-06").EvaluateAsync(Context(workflow), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		new RemediationRegistry().CanRemediate(result).Should().BeFalse();
	}

	[Fact]
	public async Task CI12_BumpsEverySubActionInEveryWorkflow()
	{
		const string codeQlPath = ".github/workflows/codeql.yml";
		const string workflow = """
			jobs:
			  analyze:
			    steps:
			      - uses: github/codeql-action/init@v2
			      - uses: github/codeql-action/analyze@v2
			""";

		var seedFile = Path.Combine(_root, "seed-action-versions.json");
		Directory.CreateDirectory(_root);
		await File.WriteAllTextAsync(
			seedFile,
			"""{"github/codeql-action":"v4"}""",
			TestContext.Current.CancellationToken);

		var previous = ActionVersionCatalog.Default;
		ActionVersionCatalog.Default = new ActionVersionCatalog(seedFile);

		try
		{
			WriteFile(codeQlPath, workflow);
			var context = WorkflowContext(codeQlPath, workflow);

			var result = await Rule("CI-12").EvaluateAsync(context, TestContext.Current.CancellationToken);
			result.Passed.Should().BeFalse("the fixture pins v2 while the organization is on v4");

			Apply(result);

			var fixedWorkflow = await File.ReadAllTextAsync(
				Path.Combine(_root, codeQlPath),
				TestContext.Current.CancellationToken);

			Output.WriteLine(fixedWorkflow);

			fixedWorkflow.Should().Contain("github/codeql-action/init@v4")
				.And.Contain("github/codeql-action/analyze@v4")
				.And.NotContain("@v2", "every usage moves, not just the first");

			var reassessed = await Rule("CI-12")
				.EvaluateAsync(WorkflowContext(codeQlPath, fixedWorkflow), TestContext.Current.CancellationToken);

			reassessed.Passed.Should().BeTrue("the remediation is supposed to satisfy the rule it came from");
		}
		finally
		{
			ActionVersionCatalog.Default = previous;
		}
	}

	[Fact]
	public async Task VER04_DeletesAToolManifestThatOnlyEverPinnedNbgv()
	{
		const string manifestPath = ".config/dotnet-tools.json";
		const string manifest = """{"version":1,"isRoot":true,"tools":{"nbgv":{"version":"3.9.50","commands":["nbgv"]}}}""";

		WriteFile(manifestPath, manifest);

		var result = await Rule("VER-04")
			.EvaluateAsync(WorkflowContext(manifestPath, manifest), TestContext.Current.CancellationToken);
		result.Passed.Should().BeFalse("the fixture pins a tool nothing invokes");

		Apply(result);

		File.Exists(Path.Combine(_root, ".config", "dotnet-tools.json"))
			.Should().BeFalse("the remediation is a deletion, so the file has to be gone");

		var reassessed = await Rule("VER-04").EvaluateAsync(
			new RepositoryContext
			{
				FullName = "panoramicdata/Sample",
				Name = "Sample",
				DefaultBranch = "main",
				CurrentBranch = "main",
				Options = new RepoOptions(),
				FilePaths = [],
				FileContents = []
			},
			TestContext.Current.CancellationToken);

		reassessed.Passed.Should().BeTrue("the remediation is supposed to satisfy the rule it came from");
	}

	private static RepositoryContext WorkflowContext(string path, string content) => new()
	{
		FullName = "panoramicdata/Sample",
		Name = "Sample",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [path],
		FileContents = new() { [path] = content }
	};

	[Fact]
	public async Task TST05_CreatesAMissingRunnerConfigForEveryTestProject()
	{
		var context = TestProjectContext(runnerConfigs: []);
		var result = await Rule("TST-05").EvaluateAsync(context, TestContext.Current.CancellationToken);

		Apply(result);

		foreach (var project in new[] { "Acme.Widget.Test", "Acme.Widget.Integration.Tests" })
		{
			var written = await File.ReadAllTextAsync(
				Path.Combine(_root, project, "xunit.runner.json"),
				TestContext.Current.CancellationToken);

			written.Should().Contain("\"failSkips\": true", $"{project} had no config at all");
		}
	}

	[Fact]
	public async Task TST05_AddsFailSkipsToAnExistingConfigWithoutDiscardingIt()
	{
		var context = TestProjectContext(runnerConfigs: new()
		{
			["Acme.Widget.Test/xunit.runner.json"] = """{"parallelizeTestCollections": false}""",
			["Acme.Widget.Integration.Tests/xunit.runner.json"] = """{"failSkips": true}"""
		});

		var result = await Rule("TST-05").EvaluateAsync(context, TestContext.Current.CancellationToken);

		Apply(result);

		var written = await File.ReadAllTextAsync(
			Path.Combine(_root, "Acme.Widget.Test", "xunit.runner.json"),
			TestContext.Current.CancellationToken);

		written.Should().Contain("\"failSkips\": true");
		written.Should().Contain("parallelizeTestCollections", "the rest of the config is not this rule's to discard");
	}

	[Fact]
	public async Task TST05_IsAiOnlyWhenAProjectHasDeliberatelyTurnedFailSkipsOff()
	{
		var context = TestProjectContext(runnerConfigs: new()
		{
			["Acme.Widget.Test/xunit.runner.json"] = """{"failSkips": false}""",
			["Acme.Widget.Integration.Tests/xunit.runner.json"] = """{"failSkips": true}"""
		});

		var result = await Rule("TST-05").EvaluateAsync(context, TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse();
		new RemediationRegistry().CanRemediate(result).Should()
			.BeFalse("an explicit false is somebody's decision, not an omission to fill in");
	}

	/// <summary>
	/// A repository still on xunit v2 that took the TST-04 remediation: the Microsoft.Testing.Platform
	/// collector replaced coverlet, so no test can be discovered any more. Reproduces
	/// panoramicdata/winforms-datavisualization, and the state panoramicdata/LogicMonitor.Datamart was
	/// in between ae96519 and 8f12693.
	/// </summary>
	[Fact]
	public async Task TST04_TakesTheMtpCollectorBackOutOfAVsTestRepository()
	{
		const string testProjectPath = "Acme.Widget.Test/Acme.Widget.Test.csproj";
		var files = new Dictionary<string, string>
		{
			["Directory.Packages.props"] =
				"<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup>"
				+ "<PackageVersion Include=\"xunit\" Version=\"2.9.3\" />"
				+ "<PackageVersion Include=\"Microsoft.Testing.Extensions.CodeCoverage\" Version=\"18.11.2\" />"
				+ "</ItemGroup></Project>",
			[testProjectPath] =
				"<Project><ItemGroup>"
				+ "<PackageReference Include=\"xunit\" />"
				+ "<PackageReference Include=\"Microsoft.Testing.Extensions.CodeCoverage\" PrivateAssets=\"all\" />"
				+ "</ItemGroup></Project>"
		};

		var result = await Rule("TST-04")
			.EvaluateAsync(FilesContext(files), TestContext.Current.CancellationToken);

		result.Passed.Should().BeFalse("the fixture is meant to violate the rule");
		Apply(result);

		var fixedProps = await ReadFixedAsync("Directory.Packages.props");
		var fixedProject = await ReadFixedAsync(testProjectPath);

		fixedProps.Should().NotContain(
			"Microsoft.Testing.Extensions.CodeCoverage",
			"the pin is the last thing putting Microsoft.Testing.Platform in the graph once the reference is gone");
		fixedProject.Should().NotContain("Microsoft.Testing.Extensions.CodeCoverage");
		fixedProject.Should().Contain("coverlet.collector", "coverlet is the collector that works under VSTest");

		var reassessed = await Rule("TST-04").EvaluateAsync(
			FilesContext(
				new Dictionary<string, string>
				{
					["Directory.Packages.props"] = fixedProps,
					[testProjectPath] = fixedProject
				},
				write: false),
			TestContext.Current.CancellationToken);

		reassessed.Passed.Should().BeTrue("the remediation is supposed to satisfy the rule it came from");
		reassessed.IsApplicable.Should().BeFalse("the repository is still on VSTest, so the rule has nothing left to ask for");
	}

	/// <summary>
	/// Reads a file back out of the working copy after a remediation has edited it.
	/// </summary>
	private async Task<string> ReadFixedAsync(string relativePath)
	{
		var content = await File.ReadAllTextAsync(
			Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
			TestContext.Current.CancellationToken).ConfigureAwait(false);

		Output.WriteLine(content);
		return content;
	}

	/// <summary>
	/// Builds a context from an explicit set of files, writing them to the working copy unless the
	/// caller is re-assessing content it has just read back out of it.
	/// </summary>
	private RepositoryContext FilesContext(Dictionary<string, string> files, bool write = true)
	{
		if (write)
		{
			foreach (var file in files)
			{
				WriteFile(file.Key, file.Value);
			}
		}

		return new RepositoryContext
		{
			FullName = "panoramicdata/Sample",
			Name = "Sample",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files
		};
	}

	/// <summary>
	/// Writes the workflow to disk, assesses it, applies the remediation, and re-assesses — returning
	/// the fixed workflow only once the rule it was meant to satisfy actually passes.
	/// </summary>
	private async Task<string> FixAsync(string ruleId, string workflow)
	{
		var context = Context(workflow);
		var result = await Rule(ruleId).EvaluateAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(false);

		result.Passed.Should().BeFalse("the fixture is meant to violate the rule");
		Apply(result);

		var fixedWorkflow = await File.ReadAllTextAsync(
			Path.Combine(_root, _ciPath),
			TestContext.Current.CancellationToken).ConfigureAwait(false);

		Output.WriteLine(fixedWorkflow);

		var reassessed = await Rule(ruleId)
			.EvaluateAsync(Context(fixedWorkflow, write: false), TestContext.Current.CancellationToken)
			.ConfigureAwait(false);
		reassessed.Passed.Should().BeTrue("the remediation is supposed to satisfy the rule it came from");

		return fixedWorkflow.Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	/// <summary>
	/// Applies the registered remediation for a failed result, requiring that one exists and that it
	/// reports having changed at least one file.
	/// </summary>
	private void Apply(RuleResult result)
	{
		var registry = new RemediationRegistry();
		registry.CanRemediate(result).Should().BeTrue($"{result.RuleId} should offer an auto-fix here");

		var applied = new List<string>();
		registry.Get(result.RuleId)!.Apply(_root, result, applied, Output.WriteLine);

		applied.Should().NotBeEmpty("a remediation that changes nothing is not a fix");
	}

	private static IRule Rule(string ruleId) => RuleRegistry.Rules.First(rule => rule.RuleId == ruleId);

	/// <summary>
	/// Builds a context for a repository whose only interesting file is the CI workflow.
	/// </summary>
	private RepositoryContext Context(string workflow, bool write = true)
	{
		if (write)
		{
			WriteFile(_ciPath, workflow);
		}

		return new RepositoryContext
		{
			FullName = "panoramicdata/Sample",
			Name = "Sample",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [_ciPath],
			FileContents = new() { [_ciPath] = workflow }
		};
	}

	/// <summary>
	/// Builds a context for a repository with two test projects and the given runner configs.
	/// </summary>
	private RepositoryContext TestProjectContext(Dictionary<string, string> runnerConfigs)
	{
		const string project = "<Project><ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup></Project>";

		var files = new Dictionary<string, string>
		{
			["Acme.Widget.Test/Acme.Widget.Test.csproj"] = project,
			["Acme.Widget.Integration.Tests/Acme.Widget.Integration.Tests.csproj"] = project
		};

		foreach (var config in runnerConfigs)
		{
			files[config.Key] = config.Value;
		}

		foreach (var file in files)
		{
			WriteFile(file.Key, file.Value);
		}

		return new RepositoryContext
		{
			FullName = "panoramicdata/Sample",
			Name = "Sample",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files
		};
	}

	private void WriteFile(string relativePath, string content)
	{
		var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		File.WriteAllText(fullPath, content);
	}
}
