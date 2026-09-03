using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the repository is set up in Codacy and that Codacy has actually analysed it.
/// </summary>
/// <remarks>
/// This rule asks one question and no more. What Codacy then found belongs to CQ-05 (open issues)
/// and CQ-06 (per-file grades). Folding all three in here meant two rules fired on the same ten
/// issues, and the failure read "minimum file grade F, total issues 0" — a sentence that contradicts
/// itself to anyone looking at the Codacy issues page, and which named neither the file nor the
/// duplication that actually caused the grade.
/// </remarks>
public class CodacyConfiguredRule : RuleBase
{
	private readonly ICodacyFileGradeService _fileGradeService;

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyConfiguredRule"/> class.
	/// </summary>
	public CodacyConfiguredRule()
		: this(new CodacyFileGradeService())
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyConfiguredRule"/> class with an explicit
	/// file grade service (for testing).
	/// </summary>
	public CodacyConfiguredRule(ICodacyFileGradeService fileGradeService)
	{
		_fileGradeService = fileGradeService;
	}

	/// <inheritdoc />
	public override string RuleId => "CQ-03";

	/// <inheritdoc />
	public override string RuleName => "Codacy configured";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var codacy = context.Options.Codacy;
		if (codacy is null || string.IsNullOrWhiteSpace(codacy.ApiToken))
		{
			// No token means no API path to ask, so the repository's own files are the whole standard.
			return HasLocalCodacyEvidence(context)
				? Pass("Codacy is configured.")
				: Fail(
					"No Codacy configuration found (.codacy/ directory, .codacy.yml, or Codacy badge in README).",
					new RuleAdvisory
					{
						Summary = "Set up Codacy integration and add .codacy/cli.sh or .codacy.yml.",
						Detail = "No Codacy configuration was found. Set up Codacy integration at app.codacy.com and add a `.codacy.yml` or `.codacy/cli.sh` file to the repository root.",
						Data = new() { ["expected_files"] = new[] { ".codacy.yml", ".codacy.yaml", ".codacy/cli.sh" } }
					});
		}

		var parts = context.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return Fail(
				$"Repository full name '{context.FullName}' is invalid for Codacy provider lookup.",
				new RuleAdvisory
				{
					Summary = "Set RepositoryContext.FullName to 'organization/repository'.",
					Detail = $"The repository full name `{context.FullName}` could not be split into organization/repository. Ensure `RepositoryContext.FullName` is in `owner/repo` format.",
					Data = new() { ["full_name"] = context.FullName }
				});
		}

		CodacyFileGradeReport report;
		try
		{
			report = await _fileGradeService
				.GetGradesAsync(codacy.ApiToken!, parts[0], parts[1], context.DefaultBranch, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// An unreachable API leaves the question unanswered, not answered "yes". Local evidence — a
			// .codacy.yml, or a badge in the README — says somebody once set Codacy up. It says nothing
			// about whether the integration works today, and reporting it as a pass hid a token that
			// could not see a single repository: 69 repositories showed green on the strength of a
			// badge, so the split between pass and fail was by README content rather than by Codacy.
			if (HasLocalCodacyEvidence(context))
			{
				return NotApplicable(
					$"Codacy is configured locally, but the integration could not be evaluated: {ex.Message}");
			}

			return Fail(
				$"Failed to reach Codacy to confirm the repository is set up: {ex.Message}",
				new RuleAdvisory
				{
					Summary = "Verify Codacy token validity and repository/provider mapping (GitHub provider).",
					Detail = $"An exception occurred when calling the Codacy API: `{ex.Message}`. Verify that the API token is valid and that the repository is correctly mapped to the GitHub provider. Alternatively, add a `.codacy.yml` file or Codacy badge to the README.",
					Data = new() { ["exception"] = ex.Message }
				});
		}

		if (!report.IsTracked)
		{
			return Fail(
				"Codacy does not know this repository — it has not been added.",
				new RuleAdvisory
				{
					Summary = $"Add {context.FullName} to Codacy at app.codacy.com.",
					Detail = $"""
						Codacy holds no repository named `{parts[1]}` in `{parts[0]}`: both the file listing and
						the repository endpoint answered 404 for that name.

						Add it at https://app.codacy.com/gh/{parts[0]}/dashboard, then let the first analysis
						of `{context.DefaultBranch}` finish.

						If the repository is visibly there in Codacy, check the name for a case difference —
						Codacy's paths are case-sensitive, and the dashboard takes a repository's identity from
						the `RepositoryUrl` declared in its published package, which is where a wrong casing
						enters. GitHub routes case-insensitively, so the typo surfaces here first.
						""",
					Data = new()
					{
						["repository"] = context.FullName,
						["default_branch"] = context.DefaultBranch
					}
				});
		}

		// An analysis in flight answers this rule's question — the integration is demonstrably working
		// — and is why nothing is graded yet. Without this the rule read a first analysis still running
		// as "analysed nothing" and told the reader to go and run the analysis that was already running.
		if (report.AnalysisState is { IsAnalysing: true } analysing)
		{
			return Pass(
				CodacyFreshness.Describe(analysing, context.HeadSha)
					?? "Codacy is re-analysing this repository.");
		}

		if (!report.GradedFiles.Any())
		{
			return Fail(
				$"Codacy has the repository but has analysed nothing on {context.DefaultBranch}.",
				new RuleAdvisory
				{
					Summary = $"Run a Codacy analysis of {context.DefaultBranch} for {context.FullName}.",
					Detail = $"""
						Codacy lists `{context.DefaultBranch}` but graded none of its files, so no analysis has
						completed. A repository in this state looks configured from the outside while every
						quality figure drawn from it is an absence rather than a measurement.

						Check that `{context.DefaultBranch}` is enabled for analysis in the repository's Codacy
						settings, and that a commit has been pushed since the integration was added.
						""",
					Data = new()
					{
						["repository"] = context.FullName,
						["default_branch"] = context.DefaultBranch,
						["listed_files"] = report.Files.Count
					}
				});
		}

		// Which commit the analysis describes, and whether another is running, belong in this sentence:
		// it is the one place that asserts the integration is working, and "has analysed main" without
		// a date is a claim nothing keeps current.
		var freshness = CodacyFreshness.Describe(report.AnalysisState, context.HeadSha)
			?? CodacyFreshness.DescribeLastAnalysis(report.AnalysisState);

		return Pass(freshness is null
			? $"Codacy is configured and has analysed {context.DefaultBranch}."
			: $"Codacy is configured and has analysed {context.DefaultBranch}. {freshness}");
	}

	/// <summary>
	/// Checks for local evidence of Codacy integration: config files
	/// (.codacy.yml, .codacy.yaml, .codacy/ directory) or a Codacy badge in README.md.
	/// </summary>
	private static bool HasLocalCodacyEvidence(RepositoryContext context)
	{
		if (context.FilePaths.Any(p =>
			p.StartsWith(".codacy/", StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		if (context.FileExists(".codacy.yml") || context.FileExists(".codacy.yaml"))
		{
			return true;
		}

		var readme = context.GetFileContent("README.md");
		return readme is not null && Contains(readme, "codacy");
	}
}
