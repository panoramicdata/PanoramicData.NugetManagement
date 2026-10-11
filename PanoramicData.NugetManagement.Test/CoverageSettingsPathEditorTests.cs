using PanoramicData.NugetManagement.Services;
using YamlDotNet.RepresentationModel;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Microsoft.Testing.Extensions.CodeCoverage 18.12 rejects a relative --coverage-settings path when
/// the test host is run directly ("Provided settings file doesn't exist or is invalid"), and accepts
/// one under <c>dotnet test</c>. Both were run, so the editor changes the first and never the second.
/// </summary>
public class CoverageSettingsPathEditorTests
{
	private const string _directHost = """
		jobs:
		  coverage:
		    runs-on: [pdl-public]
		    steps:
		    - name: Run tests with coverage
		      run: >-
		        ./TeamViewer.Api.Test/bin/Debug/net10.0/TeamViewer.Api.Test
		        --coverage
		        --coverage-settings coverage.config
		        --coverage-output-format cobertura
		""";

	[Fact]
	public void ShouldMakeARelativePathAbsolute_ForADirectHostRun()
	{
		var edited = CoverageSettingsPathEditor.MakeAbsolute(_directHost)!;

		edited.Should().Contain("--coverage-settings ${{ github.workspace }}/coverage.config");
		edited.Should().NotContain("--coverage-settings coverage.config");
	}

	[Fact]
	public void ShouldChangeNothingElse()
	{
		var edited = CoverageSettingsPathEditor.MakeAbsolute(_directHost)!;

		Normalise(edited).Should().Be(Normalise(_directHost).Replace(
			"--coverage-settings coverage.config",
			"--coverage-settings ${{ github.workspace }}/coverage.config"));
	}

	[Fact]
	public void ShouldStripALeadingDotSlash()
		=> CoverageSettingsPathEditor.MakeAbsolute(_directHost.Replace("coverage.config", "./coverage.config"))!
			.Should().Contain("--coverage-settings ${{ github.workspace }}/coverage.config");

	[Fact]
	public void ShouldHandleASingleLineRun()
	{
		const string workflow = "jobs:\n  t:\n    steps:\n    - run: ./Acme.Test --coverage --coverage-settings cov/settings.xml\n";

		CoverageSettingsPathEditor.MakeAbsolute(workflow)!
			.Should().Contain("--coverage-settings ${{ github.workspace }}/cov/settings.xml");
	}

	[Fact]
	public void ShouldFixEveryDirectHostRunInTheFile()
	{
		var twice = _directHost + "\n" + _directHost.Replace("coverage.config", "other.config");

		var edited = CoverageSettingsPathEditor.MakeAbsolute(twice)!;

		edited.Should().Contain("${{ github.workspace }}/coverage.config").And.Contain("${{ github.workspace }}/other.config");
	}

	[Theory]
	[InlineData("${{ github.workspace }}/coverage.config")]
	[InlineData("$GITHUB_WORKSPACE/coverage.config")]
	[InlineData("/home/runner/work/x/coverage.config")]
	[InlineData("C:\\work\\coverage.config")]
	[InlineData("\"$GITHUB_WORKSPACE/coverage.config\"")]
	[InlineData("\"coverage.config\"")]
	public void ShouldLeaveAnAlreadyAbsoluteOrQuotedPathAlone(string path)
		=> CoverageSettingsPathEditor.MakeAbsolute(_directHost.Replace("coverage.config", path)).Should().BeNull();

	[Fact]
	public void ShouldNotTreatACommentAsAnArgument()
	{
		// The fixed workflows explain the problem in a comment that quotes the flag. Reading that as an
		// argument would flag the very file the fix just corrected.
		const string workflow = "jobs:\n  c:\n    steps:\n"
			+ "    - run: dotnet build\n"
			+ "    # Microsoft.Testing.Extensions.CodeCoverage 18.12 does not resolve a relative --coverage-settings path\n"
			+ "    # against the working directory, so pass it absolute.\n"
			+ "    - name: Cover\n      run: >-\n        ./T.Test\n        --coverage-settings ${{ github.workspace }}/coverage.config\n";

		CoverageSettingsPathEditor.MakeAbsolute(workflow).Should().BeNull();
	}

	[Fact]
	public void ShouldLeaveADotnetTestStepAlone()
	{
		// dotnet test resolves a relative path fine, so there is nothing to fix and nothing to flag.
		const string workflow = "jobs:\n  t:\n    steps:\n    - name: Test\n      run: dotnet test --no-build --coverage --coverage-settings coverage.settings.xml\n";

		CoverageSettingsPathEditor.MakeAbsolute(workflow).Should().BeNull();
	}

	[Fact]
	public void ShouldNotBeFooledByADotnetTestInAnEarlierStep()
	{
		const string workflow = "jobs:\n  t:\n    steps:\n    - name: Build\n      run: dotnet test --no-build\n"
			+ "    - name: Cover\n      run: ./Acme.Test --coverage --coverage-settings coverage.config\n";

		CoverageSettingsPathEditor.MakeAbsolute(workflow)!
			.Should().Contain("--coverage-settings ${{ github.workspace }}/coverage.config");
	}

	[Fact]
	public void ShouldPreserveCrLfLineEndings()
	{
		var crlf = Normalise(_directHost).Replace("\n", "\r\n");

		var edited = CoverageSettingsPathEditor.MakeAbsolute(crlf)!;

		edited.Replace("\r\n", string.Empty).Should().NotContain("\n");
	}

	[Fact]
	public void TheEditedWorkflow_ShouldStillParseAsYaml()
	{
		var edited = CoverageSettingsPathEditor.MakeAbsolute(_directHost)!;

		new Action(() => new YamlStream().Load(new StringReader(edited))).Should().NotThrow();
	}

	[Fact]
	public void ShouldReturnNull_WhenThereIsNothingToChange()
		=> CoverageSettingsPathEditor.MakeAbsolute("jobs:\n  t:\n    steps:\n    - run: echo hi\n").Should().BeNull();

	private static string Normalise(string text) => text.Replace("\r\n", "\n");
}
