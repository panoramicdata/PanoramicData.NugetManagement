using PanoramicData.NugetManagement.Models;

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
		if (!context.FindTestProjectFiles().Any())
		{
			return Task.FromResult(NotApplicable("No test projects found; there is no coverage to upload."));
		}

		var workflows = context.FilePaths
			.Where(IsWorkflowFile)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToList();

		var uploading = workflows.FirstOrDefault(path =>
			context.GetFileContent(path) is { } content
			&& _uploadMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase)));

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

						The token is a separate problem: CQ-07 reports whether this repository has one,
						and it cannot be created by editing files.
						""",
					Data = new()
					{
						["workflow_count"] = workflows.Count
					}
				}));
	}

	private static bool IsWorkflowFile(string path)
		=> path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
			&& (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
}
