using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// A repository whose tests need a live service declares it with <c>DefaultTestingLevel: None</c>,
/// and every rule that exists to measure or upload coverage then reports not-applicable.
/// </summary>
/// <remarks>
/// Written after CI-15 told the AI fixer to copy a coverage job into repositories whose CI
/// deliberately runs no tests: Rundeck.Api and Highlight.Api call a live Rundeck and a live Highlight
/// portal, which CI does not have, so the job it asked for could only ever fail. The rule could not
/// tell "has tests but never uploads" from "has tests CI cannot run", and the fixer acts on warnings.
/// <para>
/// The declaration is the one the app already honours when it runs tests locally
/// (<c>LocalRepoService</c> skips those projects), so a repository states this once rather than once
/// per consumer. It deliberately does not use <c>TestingTreatment: Exclude</c>, which would remove
/// the project from <c>FindTestProjectFiles()</c> and make TST-01 report that the repository has no
/// tests at all.
/// </para>
/// </remarks>
public class CoverageOptOutTests
{
	private const string TestProject = "Acme.Widget.Test/Acme.Widget.Test.csproj";

	private const string UploadingWorkflow = """
		name: CI
		jobs:
		  coverage:
		    steps:
		    - uses: codacy/codacy-coverage-reporter-action@v1
		""";

	private static RepositoryContext ContextWith(
		ProjectTestingLevel? level,
		bool withWorkflow = false,
		double? coverage = null)
		=> new()
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = withWorkflow ? [TestProject, ".github/workflows/ci.yml"] : [TestProject],
			FileContents = withWorkflow
				? new() { [TestProject] = "<Project/>", [".github/workflows/ci.yml"] = UploadingWorkflow }
				: new() { [TestProject] = "<Project/>" },
			ActionsSecretNames = [],
			LineCoveragePercent = coverage,
			RepositoryConfig = level is { } declared
				? new NugetManagementRepositoryConfig
				{
					Projects = new() { [TestProject] = new NugetManagementProjectConfig { DefaultTestingLevel = declared } }
				}
				: null
		};

	[Fact]
	public void TheDeclarationAsCommittedToARepository_ParsesAndIsHonoured()
	{
		// The exact shape committed to Rundeck.Api and Highlight.Api: no $schema (the schema file lives
		// only in this repository), and a comment explaining why. The parser returns null on any
		// exception, so a file that fails to parse would silently declare nothing and the repository
		// would go on being asked to copy a coverage job. This pins that it does not.
		const string committed = """
			{
				"version": 1,
				"projects": {
					// Every test calls a live service, and CI has none.
					"Rundeck.Api.Test/Rundeck.Api.Test.csproj": {
						"defaultTestingLevel": "None"
					}
				}
			}
			""";

		var config = NugetManagementRepositoryConfigParser.Parse(committed);

		config.Should().NotBeNull("a config that fails to parse silently declares nothing");

		var context = new RepositoryContext
		{
			FullName = "test-org/Rundeck.Api",
			Name = "Rundeck.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = ["Rundeck.Api.Test/Rundeck.Api.Test.csproj"],
			FileContents = new() { ["Rundeck.Api.Test/Rundeck.Api.Test.csproj"] = "<Project/>" },
			RepositoryConfig = config
		};

		context.FindCoverageTestProjectFiles().Should().BeEmpty();
		context.FindTestProjectFiles().Should().ContainSingle();
	}

	[Fact]
	public void AProjectDeclaredNone_IsNotACoverageTestProject()
		=> ContextWith(ProjectTestingLevel.None).FindCoverageTestProjectFiles().Should().BeEmpty();

	[Theory]
	[InlineData(null)]
	[InlineData(ProjectTestingLevel.Auto)]
	[InlineData(ProjectTestingLevel.Smoke)]
	[InlineData(ProjectTestingLevel.Full)]
	public void EveryOtherLevel_RemainsACoverageTestProject(ProjectTestingLevel? level)
		=> ContextWith(level).FindCoverageTestProjectFiles().Should().ContainSingle().Which.Should().Be(TestProject);

	[Fact]
	public void TheProjectIsStillATestProject_SoTst01DoesNotReportNoTests()
	{
		// The reason this is not TestingTreatment: Exclude. The repository has tests; it just cannot
		// run them unattended, and saying it has none would be a different, false finding.
		ContextWith(ProjectTestingLevel.None).FindTestProjectFiles().Should().ContainSingle();
	}

	[Fact]
	public async Task Ci15_IsNotApplicable_WhenEveryTestProjectIsDeclaredNone()
	{
		var result = await new CodacyCoverageUploadRule()
			.EvaluateAsync(ContextWith(ProjectTestingLevel.None), CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue();
		result.Message.Should().Contain("DefaultTestingLevel");
	}

	[Fact]
	public async Task Cq07_IsNotApplicable_WhenEveryTestProjectIsDeclaredNone()
	{
		var result = await new CodacyProjectTokenRule()
			.EvaluateAsync(ContextWith(ProjectTestingLevel.None), CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
		result.Message.Should().Contain("DefaultTestingLevel");
	}

	[Fact]
	public async Task Tst10_IsNotApplicable_WhenEveryTestProjectIsDeclaredNone()
	{
		var result = await new CodeCoverageBandRule()
			.EvaluateAsync(ContextWith(ProjectTestingLevel.None, coverage: null), CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
		result.Message.Should().Contain("DefaultTestingLevel");
	}

	[Fact]
	public async Task Tst07_IsNotApplicable_WhenEveryTestProjectIsDeclaredNone()
	{
		var result = await new CodeCoverageTrendRule(new CoverageBaselineCatalog(null))
			.EvaluateAsync(ContextWith(ProjectTestingLevel.None, coverage: 50), CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
		result.Message.Should().Contain("DefaultTestingLevel");
	}

	[Fact]
	public async Task WithoutTheDeclaration_Ci15StillFails_WhenNothingUploads()
	{
		// The opt-out must not weaken the rule for everyone else.
		var result = await new CodacyCoverageUploadRule()
			.EvaluateAsync(ContextWith(null), CancellationToken.None);

		result.IsApplicable.Should().BeTrue();
		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task WithoutTheDeclaration_Tst10StillGradesRed_WhenThereIsNoFigure()
	{
		var result = await new CodeCoverageBandRule()
			.EvaluateAsync(ContextWith(null, coverage: null), CancellationToken.None);

		result.IsApplicable.Should().BeTrue();
		result.Passed.Should().BeFalse();
		result.Severity.Should().Be(AssessmentSeverity.Error);
	}

	[Fact]
	public async Task OneRunnableProjectIsEnough_ToKeepTheRulesApplicable()
	{
		// A repository with a hermetic unit-test project and an integration project declared None is
		// still measured: only the projects CI can run count, and one is enough.
		const string integration = "Acme.Widget.IntegrationTests/Acme.Widget.IntegrationTests.csproj";
		var context = new RepositoryContext
		{
			FullName = "test-org/Acme.Widget",
			Name = "Acme.Widget",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions(),
			FilePaths = [TestProject, integration],
			FileContents = new() { [TestProject] = "<Project/>", [integration] = "<Project/>" },
			RepositoryConfig = new NugetManagementRepositoryConfig
			{
				Projects = new() { [integration] = new NugetManagementProjectConfig { DefaultTestingLevel = ProjectTestingLevel.None } }
			}
		};

		context.FindCoverageTestProjectFiles().Should().ContainSingle().Which.Should().Be(TestProject);

		var result = await new CodacyCoverageUploadRule().EvaluateAsync(context, CancellationToken.None);
		result.IsApplicable.Should().BeTrue();
	}
}
