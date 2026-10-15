using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for reading a coverage report and for what a TST-10 session is given to work with.
/// </summary>
public class CoverageMeasurementTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"coverage-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private const string Report = """
		<?xml version="1.0" encoding="utf-8"?>
		<coverage line-rate="0.5">
		  <sources><source>/_/</source></sources>
		  <packages>
		    <package name="Lib">
		      <classes>
		        <class name="Lib.Client" filename="/_/Lib/Client.cs">
		          <lines>
		            <line number="10" hits="3" />
		            <line number="11" hits="0" />
		            <line number="12" hits="0" />
		            <line number="13" hits="0" />
		            <line number="20" hits="1" />
		            <line number="30" hits="0" />
		          </lines>
		        </class>
		        <class name="Lib.Client.&lt;&gt;c" filename="/_/Lib/Client.cs">
		          <lines>
		            <line number="30" hits="2" />
		          </lines>
		        </class>
		        <class name="Lib.Tests.ClientTests" filename="/_/Lib.Tests/ClientTests.cs">
		          <lines>
		            <line number="5" hits="9" />
		          </lines>
		        </class>
		      </classes>
		    </package>
		  </packages>
		</coverage>
		""";

	private static bool IsTestFile(string path) => path.StartsWith("Lib.Tests/", StringComparison.OrdinalIgnoreCase);

	[Fact]
	public void Parse_CountsLinesPerFile_AndMakesDeterministicPathsRelative()
	{
		var report = CoberturaReport.Parse(Report, @"C:\clone", IsTestFile);

		report.Files.Select(file => file.Path).Should().Equal("Lib/Client.cs");
		report.TotalLines.Should().Be(6);
		report.CoveredLines.Should().Be(3, "lines 10, 20 and 30 ran; 30 only under the lambda's generated class");
		report.LinePercent.Should().BeApproximately(50.0, 0.001);
	}

	[Fact]
	public void Parse_AMissedLineIsCovered_IfAnyClassInTheFileRanIt()
	{
		var report = CoberturaReport.Parse(Report, @"C:\clone", IsTestFile);

		report.Files.Single().Lines[30].Should().BeTrue();
		report.Files.Single().Lines[11].Should().BeFalse();
	}

	[Fact]
	public void Parse_LeavesOutTestCode_SoItCannotFlatterTheFigure()
	{
		var withTests = CoberturaReport.Parse(Report, @"C:\clone");
		var without = CoberturaReport.Parse(Report, @"C:\clone", IsTestFile);

		withTests.Files.Select(file => file.Path).Should().Contain("Lib.Tests/ClientTests.cs");
		without.Files.Select(file => file.Path).Should().NotContain("Lib.Tests/ClientTests.cs");
		without.LinePercent.Should().BeLessThan(withTests.LinePercent);
	}

	[Fact]
	public void Parse_MakesAbsoluteCloneRootPathsRelative()
	{
		var xml = Report.Replace("/_/Lib/Client.cs", "C:\\clone\\Lib\\Client.cs", StringComparison.Ordinal);

		CoberturaReport.Parse(xml, @"C:\clone", IsTestFile).Files.Single().Path.Should().Be("Lib/Client.cs");
	}

	[Fact]
	public void UncoveredRanges_CollapseRuns()
	{
		var file = new FileCoverage(
			"a.cs",
			new Dictionary<int, bool>
			{
				[1] = true, [2] = false, [3] = false, [4] = false, [6] = false, [8] = true, [9] = false, [10] = false
			});

		file.UncoveredRanges().Should().Be("2-4, 6, 9-10");
		file.Uncovered.Should().Be(6);
	}

	[Fact]
	public void Describe_SaysTheFigure_TheGap_AndWhereTheMissedLinesAre()
	{
		var text = CoberturaReport.Parse(Report, @"C:\clone", IsTestFile).Describe(target: 80);

		text.Should().Contain("50.0%").And.Contain("target is 80%");
		text.Should().Contain("About 2 more lines");
		text.Should().Contain("Lib/Client.cs: 3 of 6, lines 11-13");
	}

	[Fact]
	public void Describe_WhenTheTargetIsMet_AsksForNothingMore()
		=> CoberturaReport.Parse(Report, @"C:\clone", IsTestFile).Describe(target: 50)
			.Should().NotContain("more lines have to run");

	[Theory]
	[InlineData("<PackageReference Include=\"xunit.v3\" />", "", true)]
	[InlineData("<PackageReference Include=\"xunit\" />", "<PackageVersion Include=\"xunit.v3\" />", true)]
	[InlineData("<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>", "", true)]
	[InlineData("<PackageReference Include=\"xunit\" />", "", false)]
	[InlineData("<PackageReference Include=\"MSTest\" />", "", false)]
	public void UsesTestingPlatform_TellsTheTwoRunnersApart(string project, string packages, bool expected)
		=> CoverageMeasurement.UsesTestingPlatform(project, packages).Should().Be(expected);

	[Fact]
	public void FindTestProjects_FindsTestProjectsOnly_AndNotBuildOutput()
	{
		Directory.CreateDirectory(Path.Combine(_root, "src"));
		Directory.CreateDirectory(Path.Combine(_root, "src.Test"));
		Directory.CreateDirectory(Path.Combine(_root, "src.Test", "obj"));
		File.WriteAllText(Path.Combine(_root, "src", "src.csproj"), "<Project />");
		File.WriteAllText(Path.Combine(_root, "src.Test", "src.Test.csproj"), "<Project><ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup></Project>");
		File.WriteAllText(Path.Combine(_root, "src.Test", "obj", "src.Test.csproj"), "<Project><PackageReference Include=\"xunit.v3\" /></Project>");

		CoverageMeasurement.FindTestProjects(_root).Should().Equal("src.Test/src.Test.csproj");
	}

	[Fact]
	public void FindTestProjects_RecognisesTheIsTestProjectFlag_EvenWithoutTestInTheName()
	{
		Directory.CreateDirectory(Path.Combine(_root, "Checks"));
		File.WriteAllText(Path.Combine(_root, "Checks", "Checks.csproj"), "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");

		CoverageMeasurement.FindTestProjects(_root).Should().Equal("Checks/Checks.csproj");
	}

	private static AiToolCall Call(string name, params (string Name, string Value)[] arguments)
		=> new(name, arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal));

	[Fact]
	public void RunCoverage_IsOnlyOffered_WhenTheToolboxCanRunIt()
	{
		new AiFixToolbox(_root).Offers("run_coverage").Should().BeFalse();
		new AiFixToolbox(_root, coverage: _ => Task.FromResult("x")).Offers("run_coverage").Should().BeTrue();
		new AiFixToolbox(_root).Offers("run_build").Should().BeTrue();
	}

	[Fact]
	public async Task RunCoverage_WhenNotAvailable_IsAnErrorTheModelCanRead()
	{
		var result = await new AiFixToolbox(_root).ExecuteAsync(Call("run_coverage"), TestContext.Current.CancellationToken);

		result.IsError.Should().BeTrue();
		result.Content.Should().Contain("run_coverage");
	}

	[Fact]
	public async Task WritableFolders_AllowAnythingUnderThemAndNothingElse()
	{
		Directory.CreateDirectory(Path.Combine(_root, "Lib.Tests"));
		Directory.CreateDirectory(Path.Combine(_root, "Lib"));
		File.WriteAllText(Path.Combine(_root, "Lib", "Client.cs"), "x");
		var toolbox = new AiFixToolbox(_root, writableFolders: ["Lib.Tests"]);

		var inside = await toolbox.ExecuteAsync(
			Call("write_file", ("path", "Lib.Tests/NewTests.cs"), ("content", "class T {}")),
			TestContext.Current.CancellationToken);
		var outside = await toolbox.ExecuteAsync(
			Call("write_file", ("path", "Lib/Client.cs"), ("content", "changed")),
			TestContext.Current.CancellationToken);
		var lookalike = await toolbox.ExecuteAsync(
			Call("write_file", ("path", "Lib.Tests.Extra/X.cs"), ("content", "x")),
			TestContext.Current.CancellationToken);

		inside.IsError.Should().BeFalse();
		outside.IsError.Should().BeTrue();
		outside.Content.Should().Contain("Lib.Tests/");
		lookalike.IsError.Should().BeTrue("a sibling whose name merely starts the same is not inside the folder");
		(await File.ReadAllTextAsync(Path.Combine(_root, "Lib", "Client.cs"), TestContext.Current.CancellationToken)).Should().Be("x");
	}

	[Fact]
	public void ParseFailedTests_ReadsBothRunnersWording()
	{
		var output = """
			failed Lib.Tests.ClientTests.Calls_Live_Service (12ms)
			  Xunit.XunitException: boom
			Failed Lib.Tests.OtherTests.NeedsKey [14 ms]
			  Error Message: x
			passed Lib.Tests.ClientTests.Fine (1ms)
			  failed: 2
			""";

		CoverageMeasurement.ParseFailedTests(output)
			.Should().BeEquivalentTo(["Lib.Tests.ClientTests.Calls_Live_Service", "Lib.Tests.OtherTests.NeedsKey"]);
	}

	[Fact]
	public void ParseFailedTests_IgnoresTheSummaryLine()
		=> CoverageMeasurement.ParseFailedTests("Test run summary: Failed!\n  total: 3\n  failed: 1\n  succeeded: 2")
			.Should().BeEmpty("'failed: 1' is a count, not a test");

	[Fact]
	public async Task NewFilesOnly_AllowsACreatedFile_ToBeEditedAgain_ButNeverAnExistingOne()
	{
		Directory.CreateDirectory(Path.Combine(_root, "Lib.Tests"));
		File.WriteAllText(Path.Combine(_root, "Lib.Tests", "Existing.cs"), "original");
		var toolbox = new AiFixToolbox(_root, writableFolders: ["Lib.Tests"], newFilesOnly: true);

		var editExisting = await toolbox.ExecuteAsync(
			Call("replace_in_file", ("path", "Lib.Tests/Existing.cs"), ("old_text", "original"), ("new_text", "weakened")),
			TestContext.Current.CancellationToken);
		var overwriteExisting = await toolbox.ExecuteAsync(
			Call("write_file", ("path", "Lib.Tests/Existing.cs"), ("content", "gone")),
			TestContext.Current.CancellationToken);
		var create = await toolbox.ExecuteAsync(
			Call("write_file", ("path", "Lib.Tests/New.cs"), ("content", "v1")),
			TestContext.Current.CancellationToken);
		var editOwn = await toolbox.ExecuteAsync(
			Call("replace_in_file", ("path", "Lib.Tests/New.cs"), ("old_text", "v1"), ("new_text", "v2")),
			TestContext.Current.CancellationToken);

		editExisting.IsError.Should().BeTrue();
		editExisting.Content.Should().Contain("read-only");
		overwriteExisting.IsError.Should().BeTrue();
		create.IsError.Should().BeFalse();
		editOwn.IsError.Should().BeFalse("a file the session made is the session's to refine");
		(await File.ReadAllTextAsync(Path.Combine(_root, "Lib.Tests", "Existing.cs"), TestContext.Current.CancellationToken))
			.Should().Be("original");
	}

	[Fact]
	public async Task AfterWrite_FeedbackIsAppendedToTheResultOfTheWriteThatCausedIt()
	{
		Directory.CreateDirectory(_root);
		File.WriteAllText(Path.Combine(_root, "Existing.cs"), "old");
		var seen = new List<string>();

		var toolbox = new AiFixToolbox(
			_root,
			afterWrite: (path, _) =>
			{
				seen.Add(path);
				return Task.FromResult<string?>("IT DOES NOT COMPILE.");
			});

		var write = await toolbox.ExecuteAsync(
			Call("write_file", ("path", "New.cs"), ("content", "x")),
			TestContext.Current.CancellationToken);
		var replace = await toolbox.ExecuteAsync(
			Call("replace_in_file", ("path", "Existing.cs"), ("old_text", "old"), ("new_text", "new")),
			TestContext.Current.CancellationToken);
		var refused = await toolbox.ExecuteAsync(
			Call("replace_in_file", ("path", "Existing.cs"), ("old_text", "absent"), ("new_text", "y")),
			TestContext.Current.CancellationToken);

		write.Content.Should().Be("Wrote New.cs.\nIT DOES NOT COMPILE.");
		replace.Content.Should().EndWith("IT DOES NOT COMPILE.");
		refused.IsError.Should().BeTrue();
		seen.Should().Equal("New.cs", "Existing.cs");
	}

	[Fact]
	public async Task AfterWrite_ReturningNull_LeavesTheResultAsItWas()
	{
		Directory.CreateDirectory(_root);

		var result = await new AiFixToolbox(_root, afterWrite: (_, _) => Task.FromResult<string?>(null)).ExecuteAsync(
			Call("write_file", ("path", "New.txt"), ("content", "x")),
			TestContext.Current.CancellationToken);

		result.Content.Should().Be("Wrote New.txt.");
	}

	private sealed class SlowModel : IChatModel
	{
		public int Turns { get; private set; }

		public async Task<AiModelTurn> NextAsync(
			string systemPrompt,
			IReadOnlyList<AiMessage> conversation,
			IReadOnlyList<AiToolSpec> tools,
			Action<AiStreamDelta>? onDelta,
			CancellationToken cancellationToken)
		{
			Turns++;
			await Task.Delay(60, cancellationToken).ConfigureAwait(false);

			return new AiModelTurn("thinking out loud", []);
		}
	}

	/// <summary>
	/// Turns bound how much a model may do, not how long it takes. A session that outlives its clock stops,
	/// reports that it did, and does not throw.
	/// </summary>
	[Fact]
	public async Task ASessionThatOutlivesItsTimeBudget_StopsAndSaysSo()
	{
		Directory.CreateDirectory(_root);
		var model = new SlowModel();
		var lines = new List<string>();

		var session = new AiFixSession(
			model,
			new AiFixToolbox(_root),
			new AiFixOptions { MaxTurnsPerAttempt = 100, MaxAttempts = 5, TimeBudget = TimeSpan.FromMilliseconds(150) },
			lines.Add);

		var outcome = await session.RunAsync(
			new AiFixRequest("o/r", "X-01", "Rule", "Do it.", "System."),
			_ => Task.FromResult(new AiRuleCheck(false, "no")),
			TestContext.Current.CancellationToken);

		outcome.Succeeded.Should().BeFalse();
		model.Turns.Should().BeLessThan(10, "a hundred turns of sixty milliseconds each would be six seconds");
		lines.Should().Contain(line => line.Contains("budget", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void InCoverageMode_RunTestsIsNotOffered_BecauseRunCoverageSaysWhatItNeedsTo()
	{
		var coverage = new AiFixToolbox(_root, coverage: _ => Task.FromResult("x"));
		var plain = new AiFixToolbox(_root);

		coverage.Offers("run_tests").Should().BeFalse();
		coverage.Offers("run_coverage").Should().BeTrue();
		plain.Offers("run_tests").Should().BeTrue();
		plain.Offers("run_coverage").Should().BeFalse();
	}

	[Fact]
	public async Task WritableFolders_AlsoGovernReplaceInFile()
	{
		Directory.CreateDirectory(Path.Combine(_root, "Lib"));
		File.WriteAllText(Path.Combine(_root, "Lib", "Client.cs"), "x");

		var result = await new AiFixToolbox(_root, writableFolders: ["Lib.Tests"]).ExecuteAsync(
			Call("replace_in_file", ("path", "Lib/Client.cs"), ("old_text", "x"), ("new_text", "y")),
			TestContext.Current.CancellationToken);

		result.IsError.Should().BeTrue();
	}

	private static RuleResult Tst10(params (string Key, object Value)[] data) => new()
	{
		RuleId = "TST-10",
		RuleName = "Code coverage meets the estate band",
		Category = AssessmentCategory.Testing,
		Severity = AssessmentSeverity.Error,
		Passed = false,
		Message = "m",
		Advisory = new RuleAdvisory
		{
			Summary = "s",
			Detail = "d",
			Data = data.ToDictionary(pair => pair.Key, pair => pair.Value)
		}
	};

	private static RepositoryDashboardRow Row(RuleResult result) => new()
	{
		RepositoryFullName = "panoramicdata/Sample",
		Organization = "panoramicdata",
		IsClonedLocally = true,
		LocalPath = @"C:\clones\Sample",
		Assessment = new RepoAssessment
		{
			RepositoryFullName = "panoramicdata/Sample",
			DefaultBranch = "main",
			AssessedAtUtc = DateTimeOffset.UtcNow,
			RuleResults = [result]
		}
	};

	/// <summary>
	/// With no coverage figure the fix is CI's upload or its token, and no test can supply either, so the
	/// model is not offered it. A figure that is merely low is exactly what tests can move.
	/// </summary>
	[Fact]
	public void Tst10WithNoFigure_IsNotWorkForTheModel_ButALowFigureIs()
	{
		var registry = new RemediationRegistry();

		AiFixCandidates.For(Row(Tst10(("band", "RED"))), registry).Should().BeEmpty();
		AiFixCandidates.For(Row(Tst10(("band", "AMBER"), ("measured_line", 25.0))), registry)
			.Select(target => target.RuleId).Should().Equal("TST-10");
	}
}
