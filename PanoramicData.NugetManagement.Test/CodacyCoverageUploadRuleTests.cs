using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// CI-15: a repository that measures coverage also sends it somewhere the estate can read.
/// </summary>
public class CodacyCoverageUploadRuleTests
{
	private const string WorkflowWithUpload = """
		name: CI
		jobs:
		  coverage:
		    steps:
		    - uses: codacy/codacy-coverage-reporter-action@v1
		      with:
		        project-token: ${{ secrets.CODACY_PROJECT_TOKEN }}
		""";

	private const string WorkflowWithoutUpload = """
		name: CI
		jobs:
		  build:
		    steps:
		    - run: dotnet test
		""";

	private static RepositoryContext ContextWith(
		Dictionary<string, string> files,
		bool hasTestProject = true)
	{
		var paths = new List<string>(files.Keys);
		var contents = new Dictionary<string, string>(files);

		if (hasTestProject)
		{
			paths.Add("Acme.Widget.Test/Acme.Widget.Test.csproj");
			contents["Acme.Widget.Test/Acme.Widget.Test.csproj"] = "<Project/>";
		}

		return new RepositoryContext
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = paths,
			FileContents = contents
		};
	}

	private static async Task<RuleResult> EvaluateAsync(
		Dictionary<string, string> files,
		bool hasTestProject = true)
		=> await new CodacyCoverageUploadRule()
			.EvaluateAsync(ContextWith(files, hasTestProject), CancellationToken.None)
			.ConfigureAwait(false);

	[Fact]
	public async Task NoTestProjects_IsNotApplicable()
	{
		var result = await EvaluateAsync([], hasTestProject: false);

		result.IsApplicable.Should().BeFalse();
	}

	[Fact]
	public async Task AWorkflowThatUploadsCoverage_Passes()
	{
		var result = await EvaluateAsync(new() { [".github/workflows/ci.yml"] = WorkflowWithUpload });

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task NoWorkflowUploadsCoverage_Fails()
	{
		var result = await EvaluateAsync(new() { [".github/workflows/ci.yml"] = WorkflowWithoutUpload });

		result.Passed.Should().BeFalse();
		result.Advisory.Should().NotBeNull();
	}

	[Fact]
	public async Task TheUploadIsFoundInAnyWorkflow_NotJustCiYml()
	{
		var result = await EvaluateAsync(new()
		{
			[".github/workflows/ci.yml"] = WorkflowWithoutUpload,
			[".github/workflows/coverage.yml"] = WorkflowWithUpload
		});

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TheBashReporterCounts_NotOnlyTheAction()
	{
		// Some repositories pipe the reporter script rather than using the action. Both upload.
		var script = "jobs:\n  x:\n    steps:\n    - run: bash <(curl -Ls https://coverage.codacy.com/get.sh) report -r cov.xml\n";

		var result = await EvaluateAsync(new() { [".github/workflows/ci.yml"] = script });

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task NoWorkflowsAtAll_Fails()
	{
		// A repository with tests and no workflow uploads nothing. CI-01 reports the missing
		// workflow; this reports the missing coverage, and both are true.
		var result = await EvaluateAsync([]);

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public void RuleIdIsCi15()
		=> new CodacyCoverageUploadRule().RuleId.Should().Be("CI-15");
}
