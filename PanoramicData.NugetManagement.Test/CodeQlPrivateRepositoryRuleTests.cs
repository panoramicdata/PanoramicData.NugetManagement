using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using YamlDotNet.RepresentationModel;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// CI-18: a CodeQL job in a private repository fails at the upload step without GitHub Advanced
/// Security, so it is guarded on the repository being public.
/// </summary>
public class CodeQlPrivateRepositoryRuleTests
{
	private const string _codeql = "name: CodeQL\non:\n  push:\n    branches: [ main ]\njobs:\n  analyze:\n    name: Analyze\n    runs-on: ubuntu-latest\n    steps:\n    - uses: github/codeql-action/init@v4\n";

	[Fact]
	public async Task ShouldFail_ForAnUnguardedCodeQlWorkflowInAPrivateRepository()
	{
		var result = await Evaluate(isPrivate: true, _codeql);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data["remediation_type"].Should().Be("replace_regex_in_file");
	}

	[Fact]
	public async Task ShouldPass_ForAPublicRepository()
		=> (await Evaluate(isPrivate: false, _codeql)).Passed.Should().BeTrue();

	[Fact]
	public async Task ShouldNotApply_WhenVisibilityIsUnknown()
		=> (await Evaluate(isPrivate: null, _codeql)).IsApplicable.Should().BeFalse();

	[Fact]
	public async Task ShouldPass_WhenTheJobIsAlreadyGuarded()
		=> (await Evaluate(isPrivate: true, _codeql.Replace("  analyze:\n", "  analyze:\n    if: ${{ !github.event.repository.private }}\n"))).Passed.Should().BeTrue();

	[Fact]
	public async Task ShouldPass_WhenThereIsNoCodeQlWorkflow()
		=> (await Evaluate(isPrivate: true, "name: CI\njobs:\n  build:\n    steps:\n    - run: dotnet build\n")).Passed.Should().BeTrue();

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public async Task TheFix_ShouldGuardTheJobAndLeaveValidYaml(string newline)
	{
		var workflow = _codeql.Replace("\n", newline);
		var result = await Evaluate(isPrivate: true, workflow);
		var data = result.Advisory!.Data;

		var fixedWorkflow = Regex.Replace(workflow, ((string[])data["patterns"])[0], ((string[])data["replacements"])[0]);

		fixedWorkflow.Should().Contain($"  analyze:{newline}    if: ${{{{ !github.event.repository.private }}}}{newline}    name: Analyze");
		new Action(() => new YamlStream().Load(new StringReader(fixedWorkflow))).Should().NotThrow();
		(await Evaluate(isPrivate: true, fixedWorkflow)).Passed.Should().BeTrue("the fix clears the finding");
	}

	[Fact]
	public async Task ShouldNotRewriteAWorkflowWithSeveralJobs()
	{
		var twoJobs = _codeql + "  other:\n    runs-on: ubuntu-latest\n    steps:\n    - run: echo hi\n";

		var result = await Evaluate(isPrivate: true, twoJobs);

		result.Passed.Should().BeFalse();
		result.Advisory!.Data.Should().NotContainKey("remediation_type", "a second job would be left unguarded");
	}

	private static Task<RuleResult> Evaluate(bool? isPrivate, string workflow)
		=> new CodeQlPrivateRepositoryRule().EvaluateAsync(
			new RepositoryContext
			{
				FullName = "test-org/Acme.Widget",
				Name = "Acme.Widget",
				DefaultBranch = "main",
				Options = new RepoOptions(),
				IsPrivateRepository = isPrivate,
				FilePaths = [".github/workflows/codeql.yml"],
				FileContents = new() { [".github/workflows/codeql.yml"] = workflow }
			},
			CancellationToken.None);
}
