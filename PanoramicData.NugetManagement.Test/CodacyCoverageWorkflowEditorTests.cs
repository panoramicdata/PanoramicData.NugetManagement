using PanoramicData.NugetManagement.Services;
using YamlDotNet.RepresentationModel;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// The text edit behind the CI-15 fix. Its contract is that it either returns a workflow that still
/// parses and now uploads coverage, or returns null and changes nothing — it never guesses.
/// </summary>
public class CodacyCoverageWorkflowEditorTests
{
	private const string _ubuntu = """
		name: CI

		on:
		  push:
		    branches: [main]

		jobs:
		  build:
		    runs-on: ubuntu-latest
		    steps:
		    - name: Checkout
		      uses: actions/checkout@v7

		    - name: Test
		      run: dotnet test --configuration Release --no-build

		    - name: Pack
		      run: dotnet pack --configuration Release --no-build
		""";

	[Fact]
	public void Mtp_ShouldAppendTheCoverageFlagsToTheTestStep()
	{
		var edited = CodacyCoverageWorkflowEditor.AddUpload(_ubuntu, CoverageRunner.MicrosoftTestingPlatform)!;

		edited.Should().Contain(
			"run: dotnet test --configuration Release --no-build --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml");
	}

	[Fact]
	public void VsTest_ShouldAddTheCoverletCollector()
	{
		var edited = CodacyCoverageWorkflowEditor.AddUpload(_ubuntu, CoverageRunner.VsTest)!;

		edited.Should().Contain("run: dotnet test --configuration Release --no-build --collect:\"XPlat Code Coverage\"");
	}

	[Fact]
	public void ShouldAddTheUploadStepDirectlyAfterTheTestStepAtTheSameIndent()
	{
		var edited = Lines(CodacyCoverageWorkflowEditor.AddUpload(_ubuntu, CoverageRunner.MicrosoftTestingPlatform)!);

		var testRun = edited.FindIndex(l => l.Contains("run: dotnet test"));
		edited[testRun + 1].Should().BeEmpty();
		edited[testRun + 2].Should().Be("    - name: Upload coverage to Codacy");
		edited[testRun + 3].Should().Be("      continue-on-error: true");
		edited[testRun + 4].Should().Be("      uses: codacy/codacy-coverage-reporter-action@v1");
		edited.Should().Contain("        project-token: ${{ secrets.CODACY_PROJECT_TOKEN }}");
		edited.Should().Contain("        coverage-reports: '**/coverage.cobertura.xml'");

		// And the step that followed is still there, after the new one.
		edited.FindIndex(l => l.Contains("- name: Pack")).Should().BeGreaterThan(testRun + 4);
	}

	[Theory]
	[InlineData(CoverageRunner.MicrosoftTestingPlatform)]
	[InlineData(CoverageRunner.VsTest)]
	public void TheEditedWorkflow_ShouldStillParseAsYaml(CoverageRunner runner)
	{
		var edited = CodacyCoverageWorkflowEditor.AddUpload(_ubuntu, runner)!;

		var act = () => new YamlStream().Load(new StringReader(edited));

		act.Should().NotThrow("an edit that GitHub cannot parse disables the whole workflow, which is what CI-14 exists to catch");
	}

	[Fact]
	public void ShouldHandleATestStepThatIsTheLastStepInTheFile()
	{
		var workflow = "jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n    - run: dotnet test\n";

		var edited = CodacyCoverageWorkflowEditor.AddUpload(workflow, CoverageRunner.VsTest)!;

		edited.Should().Contain("    - name: Upload coverage to Codacy");
		var act = () => new YamlStream().Load(new StringReader(edited));
		act.Should().NotThrow();
	}

	[Fact]
	public void ShouldPreserveCrLfLineEndings()
	{
		var crlf = _ubuntu.Replace("\r\n", "\n").Replace("\n", "\r\n");

		var edited = CodacyCoverageWorkflowEditor.AddUpload(crlf, CoverageRunner.MicrosoftTestingPlatform)!;

		edited.Replace("\r\n", string.Empty).Should().NotContain("\n", "every line ending stays CRLF");
		edited.Should().Contain("\r\n");
	}

	[Theory]
	[InlineData("windows-latest")]
	[InlineData("macos-latest")]
	public void ShouldDeclineARunnerTheCodacyActionDoesNotDocumentSupportFor(string runner)
	{
		// The action's own documentation shows ubuntu only. An upload step that cannot run would be
		// reported as a pass by CI-15 while sending nothing.
		var workflow = _ubuntu.Replace("ubuntu-latest", runner);

		CodacyCoverageWorkflowEditor.AddUpload(workflow, CoverageRunner.MicrosoftTestingPlatform).Should().BeNull();
	}

	[Fact]
	public void ShouldAcceptASelfHostedLinuxLabelList()
		=> CodacyCoverageWorkflowEditor
			.AddUpload(_ubuntu.Replace("ubuntu-latest", "[pdl-public]"), CoverageRunner.MicrosoftTestingPlatform)
			.Should().NotBeNull();

	[Fact]
	public void ShouldDeclineAWorkflowThatAlreadyCollectsCoverage()
	{
		var workflow = _ubuntu.Replace("\r\n", "\n").Replace("--no-build\n\n    - name: Pack","--no-build --collect:\"XPlat Code Coverage\"\n\n    - name: Pack");

		CodacyCoverageWorkflowEditor.AddUpload(workflow, CoverageRunner.VsTest).Should().BeNull(
			"the coverage flags are someone's own, and a second set would conflict");
	}

	[Fact]
	public void ShouldDeclineAWorkflowWithNoSingleLineTestStep()
	{
		var workflow = "jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n    - run: |\n        dotnet build\n        dotnet test\n";

		CodacyCoverageWorkflowEditor.AddUpload(workflow, CoverageRunner.VsTest).Should().BeNull();
	}

	[Fact]
	public void ShouldDeclineAWorkflowWithNoRunsOn()
		=> CodacyCoverageWorkflowEditor
			.AddUpload("jobs:\n  build:\n    steps:\n    - run: dotnet test\n", CoverageRunner.VsTest)
			.Should().BeNull();

	[Fact]
	public void ShouldDoNothing_WhenAlreadyApplied()
	{
		var once = CodacyCoverageWorkflowEditor.AddUpload(_ubuntu, CoverageRunner.MicrosoftTestingPlatform)!;

		CodacyCoverageWorkflowEditor.AddUpload(once, CoverageRunner.MicrosoftTestingPlatform).Should().BeNull();
	}

	private static List<string> Lines(string text) => [.. text.Replace("\r\n", "\n").Split('\n')];
}
