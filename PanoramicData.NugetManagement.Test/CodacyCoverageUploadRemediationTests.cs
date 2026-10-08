using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;
using YamlDotNet.RepresentationModel;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// The CI-15 fix end to end: the rule decides when an edit is safe to offer, the remediation makes it,
/// and the rule then passes.
/// </summary>
public class CodacyCoverageUploadRemediationTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string _workflowPath = ".github/workflows/ci.yml";

	private const string _workflow = "name: CI\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n    - name: Test\n      run: dotnet test --no-build\n";

	private const string _mtpGlobalJson = "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }";

	private const string _mtpProject = "<Project><ItemGroup><PackageReference Include=\"Microsoft.Testing.Extensions.CodeCoverage\" /></ItemGroup></Project>";

	private const string _vsTestProject = "<Project><ItemGroup><PackageReference Include=\"coverlet.collector\" /></ItemGroup></Project>";

	private readonly string _root = Directory.CreateTempSubdirectory("covupload").FullName;

	public void Dispose()
	{
		Directory.Delete(_root, recursive: true);
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void ShouldBeRegisteredForCi15()
		=> new RemediationRegistry().Get("CI-15").Should().BeOfType<CodacyCoverageUploadRemediation>();

	[Fact]
	public async Task Mtp_ShouldOfferAFix_WhenTheCoverageExtensionIsReferenced()
	{
		var result = await EvaluateAsync(_workflow, _mtpProject, _mtpGlobalJson).ConfigureAwait(true);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["workflow_file"].Should().Be(_workflowPath);
		result.Advisory.Data["coverage_runner"].Should().Be("mtp");
	}

	[Fact]
	public async Task VsTest_ShouldOfferAFix_WhenCoverletIsReferenced()
	{
		var result = await EvaluateAsync(_workflow, _vsTestProject, globalJson: null).ConfigureAwait(true);

		result.Advisory!.Data["coverage_runner"].Should().Be("vstest");
	}

	[Fact]
	public async Task ShouldOfferNoFix_WhenTheCollectorThatRunnerNeedsIsNotReferenced()
	{
		// MTP runner, but only coverlet — which is inert under MTP. Adding the flags would produce no
		// report, and the upload would then send nothing.
		var result = await EvaluateAsync(_workflow, _vsTestProject, _mtpGlobalJson).ConfigureAwait(true);

		result.Passed.Should().BeFalse("CI-15 still fails: nothing uploads");
		result.Advisory!.Data.Should().NotContainKey("workflow_file");
	}

	[Fact]
	public async Task ShouldOfferNoFix_ForAWindowsRunner()
	{
		var result = await EvaluateAsync(_workflow.Replace("ubuntu-latest", "windows-latest"), _mtpProject, _mtpGlobalJson)
			.ConfigureAwait(true);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data.Should().NotContainKey("workflow_file");
	}

	[Fact]
	public async Task Fix_ShouldMakeTheRulePass_AndLeaveAWorkflowThatParses()
	{
		var failing = await EvaluateAsync(_workflow, _mtpProject, _mtpGlobalJson).ConfigureAwait(true);
		var path = Path.Combine(_root, _workflowPath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, _workflow);
		var applied = new List<string>();

		var remediation = new CodacyCoverageUploadRemediation();
		remediation.CanRemediate(failing).Should().BeTrue();
		remediation.Apply(_root, failing, applied, null);

		applied.Should().ContainSingle().Which.Should().Be(_workflowPath);
		var edited = File.ReadAllText(path);
		new Action(() => new YamlStream().Load(new StringReader(edited))).Should().NotThrow();

		var after = await EvaluateAsync(edited, _mtpProject, _mtpGlobalJson).ConfigureAwait(true);
		after.Passed.Should().BeTrue("the edited workflow now carries the Codacy reporter");
	}

	[Fact]
	public async Task Fix_ShouldNotWriteAFileItCannotEditSafely()
	{
		var failing = await EvaluateAsync(_workflow, _mtpProject, _mtpGlobalJson).ConfigureAwait(true);
		const string changedSinceAssessment = "name: CI\njobs:\n  build:\n    runs-on: windows-latest\n    steps:\n    - run: dotnet test\n";
		var path = Path.Combine(_root, _workflowPath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, changedSinceAssessment);
		var applied = new List<string>();

		new CodacyCoverageUploadRemediation().Apply(_root, failing, applied, null);

		applied.Should().BeEmpty();
		File.ReadAllText(path).Should().Be(changedSinceAssessment);
	}

	[Fact]
	public async Task CanRemediate_ShouldBeFalse_WhenTheRuleOfferedNoEdit()
	{
		var result = await EvaluateAsync(_workflow.Replace("ubuntu-latest", "windows-latest"), _mtpProject, _mtpGlobalJson)
			.ConfigureAwait(true);

		new CodacyCoverageUploadRemediation().CanRemediate(result).Should().BeFalse();
	}

	[Fact]
	public async Task Fix_ShouldRefuseAWorkflowPathThatLeavesTheClone()
	{
		var failing = await EvaluateAsync(_workflow, _mtpProject, _mtpGlobalJson).ConfigureAwait(true);
		failing.Advisory!.Data["workflow_file"] = "../outside.yml";
		var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside.yml");
		File.WriteAllText(outside, _workflow);
		try
		{
			var applied = new List<string>();
			Directory.CreateDirectory(Path.Combine(_root, "clone"));

			new CodacyCoverageUploadRemediation().Apply(Path.Combine(_root, "clone"), failing, applied, null);

			applied.Should().BeEmpty();
			File.ReadAllText(outside).Should().Be(_workflow);
		}
		finally
		{
			File.Delete(outside);
		}
	}

	private static async Task<RuleResult> EvaluateAsync(string workflow, string testProject, string? globalJson)
	{
		var files = new Dictionary<string, string>
		{
			[_workflowPath] = workflow,
			["Acme.Widget.Test/Acme.Widget.Test.csproj"] = testProject
		};

		if (globalJson is not null)
		{
			files["global.json"] = globalJson;
		}

		var context = new RepositoryContext
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. files.Keys],
			FileContents = files
		};

		return await new CodacyCoverageUploadRule().EvaluateAsync(context, CancellationToken.None).ConfigureAwait(false);
	}
}
