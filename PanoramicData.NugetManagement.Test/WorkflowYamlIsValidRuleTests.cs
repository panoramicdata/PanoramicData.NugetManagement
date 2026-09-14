using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// CI-14, the guard against a workflow file that GitHub will reject before any step runs.
/// </summary>
/// <remarks>
/// Written after a governance remediation rewrote the indentation of ~30 repositories' workflow
/// files to tabs. YAML forbids tabs for indentation, so every one of those workflows failed in zero
/// seconds on every push — CodeQL stopped analysing, and on four repositories CI stopped running
/// altogether — and nothing in this tool noticed, because no rule read a workflow as YAML.
/// </remarks>
public class WorkflowYamlIsValidRuleTests
{
	private const string ValidWorkflow = """
		name: CI
		on:
		  push:
		    branches: [main]
		jobs:
		  build:
		    runs-on: ubuntu-latest
		    steps:
		    - uses: actions/checkout@v7
		""";

	// The exact shape the remediation produced: two-space keys with tab-indented children.
	private const string TabIndentedWorkflow = "name: CI\non:\n  push:\n\tbranches: [main]\njobs:\n  build:\n\truns-on: ubuntu-latest\n";

	private static RepositoryContext ContextWith(Dictionary<string, string> workflows)
		=> new()
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [.. workflows.Keys],
			FileContents = workflows
		};

	private static async Task<RuleResult> EvaluateAsync(Dictionary<string, string> workflows)
		=> await new WorkflowYamlIsValidRule()
			.EvaluateAsync(ContextWith(workflows), CancellationToken.None)
			.ConfigureAwait(false);

	[Fact]
	public async Task NoWorkflows_IsNotApplicable()
	{
		var result = await EvaluateAsync([]);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task ValidWorkflow_Passes()
	{
		var result = await EvaluateAsync(new() { [".github/workflows/ci.yml"] = ValidWorkflow });

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TabIndentedWorkflow_Fails()
	{
		var result = await EvaluateAsync(new() { [".github/workflows/ci.yml"] = TabIndentedWorkflow });

		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Critical);
		result.Message.Should().Contain("ci.yml");
	}

	[Fact]
	public async Task TheAdvisoryNamesEveryBrokenFile()
	{
		var result = await EvaluateAsync(new()
		{
			[".github/workflows/ci.yml"] = TabIndentedWorkflow,
			[".github/workflows/codeql.yml"] = TabIndentedWorkflow,
			[".github/workflows/ok.yml"] = ValidWorkflow
		});

		result.Passed.Should().BeFalse();
		result.Advisory.Should().NotBeNull();
		result.Advisory!.Detail.Should().Contain("ci.yml").And.Contain("codeql.yml");
		result.Advisory.Detail.Should().NotContain("ok.yml");
	}

	[Fact]
	public async Task TabsInsideAStringValue_DoNotFailTheRule()
	{
		// Only indentation is forbidden. A tab inside a run: block is legitimate — a Makefile heredoc,
		// for instance — and flagging it would send somebody to "fix" working YAML.
		var withTabInValue = "name: CI\non:\n  push:\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n    - run: \"printf 'a\\tb'\"\n";

		var result = await EvaluateAsync(new() { [".github/workflows/ci.yml"] = withTabInValue });

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task AnUnfetchedWorkflow_IsNotTreatedAsBroken()
	{
		// FilePaths lists more than FileContents holds. A null content means "not read", and reporting
		// that as invalid YAML would fail every repository whose workflow simply was not fetched.
		var context = new RepositoryContext
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [".github/workflows/ci.yml"],
			FileContents = []
		};

		var result = await new WorkflowYamlIsValidRule()
			.EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public void RuleIdIsCi14()
		=> new WorkflowYamlIsValidRule().RuleId.Should().Be("CI-14");
}
