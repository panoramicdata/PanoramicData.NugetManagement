using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// A repository with tests uploads its coverage to Codacy from CI.
/// </summary>
/// <remarks>
/// TST-10 reports the outcome — that Codacy holds no coverage for this repository — but three quite
/// different causes present identically as RED: the repository was never added to Codacy, it has no
/// project token, or its CI never uploads. This rule separates the third, which is the one an agent
/// can actually fix, because the fix is to copy a job.
/// </remarks>
public class CodacyCoverageUploadRule : RuleBase
{
	/// <summary>
	/// Both ways of uploading. The action is the common one; the shell reporter is what repositories
	/// that predate it tend to use, and it uploads just as well.
	/// </summary>
	private static readonly string[] _uploadMarkers =
	[
		"codacy-coverage-reporter",
		"coverage.codacy.com"
	];

	/// <inheritdoc />
	public override string RuleId => "CI-15";

	/// <inheritdoc />
	public override string RuleName => "CI uploads coverage to Codacy";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <summary>
	/// A warning, not an error. The repository still builds and still tests; what is missing is the
	/// reporting, and TST-10 is what states the consequence.
	/// </summary>
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (NoCoverageToMeasure(context, "no coverage to upload") is { } notApplicable)
		{
			return Task.FromResult(notApplicable);
		}

		var workflows = context.FilePaths
			.Where(IsWorkflowFile)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToList();

		var uploading = workflows.FirstOrDefault(path =>
			context.GetFileContent(path) is { } content
			&& _uploadMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase)));

		var data = new Dictionary<string, object>
		{
			["workflow_count"] = workflows.Count
		};

		if (uploading is null)
		{
			AddAutomatedFix(context, workflows, data);
		}

		return Task.FromResult(uploading is not null
			? Pass($"{uploading} uploads coverage to Codacy.")
			: Fail(
				workflows.Count == 0
					? "No workflow files, so nothing uploads coverage to Codacy."
					: $"None of the {workflows.Count} workflow file(s) uploads coverage to Codacy.",
				new RuleAdvisory
				{
					Summary = "Upload coverage to Codacy from CI.",
					Detail = """
						This repository has tests but never sends their coverage anywhere, so Codacy
						holds no figure for it and TST-10 grades it RED however well tested it is.

						The reference implementation is the `coverage` job in
						panoramicdata/PanoramicData.NugetManagement's .github/workflows/ci.yml. It runs
						the tests with coverage, writes Cobertura, and uploads with
						codacy/codacy-coverage-reporter-action using a CODACY_PROJECT_TOKEN secret.

						The upload step should be continue-on-error, so a Codacy outage cannot turn a
						passing test run red.

						Do not do this if the tests cannot run in CI. A test project that calls a live
						service CI does not have (a vendor portal, a database, a device) would only turn
						the new job red. Such a repository should declare it instead, in
						PanoramicData.NugetManagement.config.json:

						  "projects": { "<Project>.Test.csproj": { "defaultTestingLevel": "None" } }

						and this rule, CQ-07 and TST-10 then report not-applicable for it. Check the
						existing ci.yml first: a comment explaining why there is no Test step is the
						signal that this is the case.

						The token is a separate problem: CQ-07 reports whether this repository has one,
						and it cannot be created by editing files.
						""",
					Data = data
				}));
	}

	/// <summary>
	/// Names the workflow and test runner when — and only when — the edit is one that can be made safely.
	/// </summary>
	/// <remarks>
	/// The workflow editor declines anything it cannot edit with certainty, and the runner's collector
	/// is added by the fix when no project references it (the MTP flags do nothing without the extension,
	/// and coverlet is inert under MTP), because an edit without it reads as a pass while uploading nothing.
	/// A repository the editor declines keeps the failing result and the prose advisory for AI.
	/// </remarks>
	private static void AddAutomatedFix(RepositoryContext context, List<string> workflows, Dictionary<string, object> data)
	{
		var mtp = context.GetFileContent("global.json") is { } globalJson
			&& globalJson.Contains(Standards.MtpTestRunnerName, StringComparison.OrdinalIgnoreCase);

		var collector = mtp ? Standards.CodeCoveragePackage : Standards.VsTestCodeCoveragePackage;
		if (context.FindCoverageTestProjectFiles().FirstOrDefault() is null)
		{
			return;
		}

		var runner = mtp ? CoverageRunner.MicrosoftTestingPlatform : CoverageRunner.VsTest;
		if (!workflows.Any(path => context.GetFileContent(path) is { } text && CodacyCoverageWorkflowEditor.AddUpload(text, runner) is not null)
			&& TryAddCoverageJob(context, workflows, runner, data))
		{
			return;
		}

		foreach (var path in workflows)
		{
			if (context.GetFileContent(path) is { } content
				&& CodacyCoverageWorkflowEditor.AddUpload(content, runner) is not null)
			{
				data["workflow_file"] = path;
				data["coverage_runner"] = mtp ? "mtp" : "vstest";
				AddCollectorData(context, collector, mtp, data);
				return;
			}
		}
	}

	/// <summary>
	/// For a repository whose CI never runs its tests: names the workflow and test project a new
	/// coverage job can be added to.
	/// </summary>
	private static bool TryAddCoverageJob(RepositoryContext context, List<string> workflows, CoverageRunner runner, Dictionary<string, object> data)
	{
		var testProject = context.FindCoverageTestProjectFiles()
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault();
		if (testProject is null)
		{
			return false;
		}

		foreach (var path in workflows)
		{
			if (context.GetFileContent(path) is { } content
				&& CodacyCoverageWorkflowEditor.AddCoverageJob(content, runner, testProject) is not null)
			{
				data["workflow_file"] = path;
				data["coverage_runner"] = runner == CoverageRunner.MicrosoftTestingPlatform ? "mtp" : "vstest";
				data["test_project"] = testProject;
			var mtp = runner == CoverageRunner.MicrosoftTestingPlatform;
			AddCollectorData(context, mtp ? Standards.CodeCoveragePackage : Standards.VsTestCodeCoveragePackage, mtp, data);
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Whether any project or build-props file references the package. Directory.Packages.props only
	/// declares a version, so it proves nothing about what a project uses.
	/// </summary>
	private static bool ProjectsReference(RepositoryContext context, string packageId)
		=> context.FilePaths
			.Where(path => (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
					|| path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
					|| path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
				&& !Path.GetFileName(path).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
			.Any(path => context.GetFileContent(path) is { } content
				&& content.Contains($"Include=\"{packageId}\"", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// When no project references the collector, records what the fix must add. Without it the MTP flags
	/// do nothing and the VSTest flag is ignored, so the edit would read as a pass while uploading nothing.
	/// </summary>
	private static void AddCollectorData(RepositoryContext context, string collector, bool mtp, Dictionary<string, object> data)
	{
		if (ProjectsReference(context, collector))
		{
			return;
		}

		var props = context.GetFileContent("Directory.Packages.props");
		data["add_collector"] = collector;
		data["collector_version"] = mtp ? Standards.CodeCoverageVersion : Standards.VsTestCodeCoverageVersion;
		data["collector_target_project"] = context.FindCoverageTestProjectFiles().OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First();
		data["uses_cpm"] = props is not null && props.Contains("ManagePackageVersionsCentrally", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsWorkflowFile(string path)
		=> path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
			&& (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
}
