using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

namespace PanoramicData.NugetManagement.Test;

public class WorkflowYamlRemediationTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string _tabbed = "name: CI\non:\n\tpush:\n\t\tbranches: [main]\n";

	private readonly string _root = Directory.CreateTempSubdirectory("wfyaml").FullName;

	public void Dispose()
	{
		Directory.Delete(_root, recursive: true);
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void ShouldBeRegisteredForCi14()
		=> new RemediationRegistry().Get("CI-14").Should().BeOfType<WorkflowYamlRemediation>();

	[Fact]
	public void ShouldReplaceIndentationTabsAndLeaveValueTabsAlone()
		=> WorkflowYamlRemediation.ReplaceLeadingTabs("a:\n\tb: \"x\ty\"\n\t\tc: 1\n")
			.Should().Be("a:\n    b: \"x\ty\"\n        c: 1\n");

	[Fact]
	public async Task ShouldRepairATabIndentedWorkflowAndPassTheRuleAfterwards()
	{
		var path = Write(".github/workflows/ci.yml", _tabbed);
		var remediation = new WorkflowYamlRemediation();
		var applied = new List<string>();

		var failing = await EvaluateAsync(".github/workflows/ci.yml", _tabbed).ConfigureAwait(true);
		remediation.CanRemediate(failing).Should().BeTrue();
		remediation.Apply(_root, failing, applied, null);

		applied.Should().ContainSingle().Which.Should().Be(".github/workflows/ci.yml");
		var after = await EvaluateAsync(".github/workflows/ci.yml", File.ReadAllText(path)).ConfigureAwait(true);
		after.Passed.Should().BeTrue("the repaired file must be what the rule accepts");
	}

	[Fact]
	public async Task ShouldLeaveAFileUnchanged_WhenRepairDoesNotMakeItParse()
	{
		const string broken = "name: CI\non: [unclosed\n";
		var path = Write(".github/workflows/ci.yml", broken);
		var applied = new List<string>();

		var failing = await EvaluateAsync(".github/workflows/ci.yml", broken).ConfigureAwait(true);
		new WorkflowYamlRemediation().Apply(_root, failing, applied, null);

		applied.Should().BeEmpty();
		File.ReadAllText(path).Should().Be(broken, "a file that cannot be shown to parse is never written");
	}

	[Fact]
	public async Task ShouldReplaceAnUnparseableCodeQlWorkflowWithTheStandardOne()
	{
		const string broken = "name: CodeQL\non: [unclosed\n";
		var path = Write(".github/workflows/codeql.yml", broken);
		var applied = new List<string>();

		var failing = await EvaluateAsync(".github/workflows/codeql.yml", broken).ConfigureAwait(true);
		new WorkflowYamlRemediation().Apply(_root, failing, applied, null);

		applied.Should().ContainSingle();
		File.ReadAllText(path).Should().Be(Standards.CodeQlWorkflowContent);
	}

	[Fact]
	public async Task ShouldRefuseAPathThatLeavesTheClone()
	{
		var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.yml");
		File.WriteAllText(outside, _tabbed);
		try
		{
			var failing = await EvaluateAsync(".github/workflows/ci.yml", _tabbed).ConfigureAwait(true);
			failing.Advisory!.Data["broken_files"] = new List<string> { $"../{Path.GetFileName(outside)}" };
			var applied = new List<string>();

			new WorkflowYamlRemediation().Apply(Path.Combine(_root, "sub"), failing, applied, null);

			applied.Should().BeEmpty();
			File.ReadAllText(outside).Should().Be(_tabbed);
		}
		finally
		{
			File.Delete(outside);
		}
	}

	private string Write(string relativePath, string content)
	{
		var path = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		return path;
	}

	private static async Task<RuleResult> EvaluateAsync(string path, string content)
	{
		var context = new RepositoryContext
		{
			FullName = "test-org/test-repo",
			Name = "test-repo",
			DefaultBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [path],
			FileContents = new Dictionary<string, string> { [path] = content }
		};

		return await new WorkflowYamlIsValidRule().EvaluateAsync(context, CancellationToken.None).ConfigureAwait(false);
	}
}
