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
				: FailNoLocalEvidence();
		}

		var parts = context.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return FailInvalidName(context.FullName);
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
			// Local evidence says Codacy was once set up, not that the integration works today, so it cannot pass.
			return HasLocalCodacyEvidence(context)
				? NotApplicable($"Codacy is configured locally, but the integration could not be evaluated: {ex.Message}")
				: FailUnreachable(ex.Message);
		}

		return EvaluateReport(context, report, parts[0], parts[1]);
	}

	private RuleResult EvaluateReport(RepositoryContext context, CodacyFileGradeReport report, string organization, string repository)
	{
		if (!report.IsTracked)
		{
			return FailUntracked(context, organization, repository);
		}

		// An analysis in flight shows the integration works, and is why nothing is graded yet.
		if (report.AnalysisState is { IsAnalysing: true } analysing)
		{
			return Pass(
				CodacyFreshness.Describe(analysing, context.HeadSha)
					?? "Codacy is re-analysing this repository.");
		}

		if (!report.GradedFiles.Any())
		{
			return FailNothingAnalysed(context, report.Files.Count);
		}

		// The one sentence asserting the integration works must say which commit it describes.
		var freshness = CodacyFreshness.Describe(report.AnalysisState, context.HeadSha)
			?? CodacyFreshness.DescribeLastAnalysis(report.AnalysisState);

		return Pass(freshness is null
			? $"Codacy is configured and has analysed {context.DefaultBranch}."
			: $"Codacy is configured and has analysed {context.DefaultBranch}. {freshness}");
	}

	private RuleResult FailNoLocalEvidence()
		=> Fail(
			"No Codacy configuration found (.codacy/ directory, .codacy.yml, or Codacy badge in README).",
			new RuleAdvisory
			{
				Summary = "Set up Codacy integration and add .codacy/cli.sh or .codacy.yml.",
				Detail = "No Codacy configuration was found. Set up Codacy integration at app.codacy.com and add a `.codacy.yml` or `.codacy/cli.sh` file to the repository root.",
				Data = new() { ["expected_files"] = new[] { ".codacy.yml", ".codacy.yaml", ".codacy/cli.sh" } }
			});

	private RuleResult FailInvalidName(string fullName)
		=> Fail(
			$"Repository full name '{fullName}' is invalid for Codacy provider lookup.",
			new RuleAdvisory
			{
				Summary = "Set RepositoryContext.FullName to 'organization/repository'.",
				Detail = $"The repository full name `{fullName}` could not be split into organization/repository. Ensure `RepositoryContext.FullName` is in `owner/repo` format.",
				Data = new() { ["full_name"] = fullName }
			});

	private RuleResult FailUnreachable(string message)
		=> Fail(
			$"Failed to reach Codacy to confirm the repository is set up: {message}",
			new RuleAdvisory
			{
				Summary = "Verify Codacy token validity and repository/provider mapping (GitHub provider).",
				Detail = $"An exception occurred when calling the Codacy API: `{message}`. Verify that the API token is valid and that the repository is correctly mapped to the GitHub provider. Alternatively, add a `.codacy.yml` file or Codacy badge to the README.",
				Data = new() { ["exception"] = message }
			});

	private RuleResult FailUntracked(RepositoryContext context, string organization, string repository)
		=> Fail(
			"Codacy does not know this repository — it has not been added.",
			new RuleAdvisory
			{
				Summary = $"Add {context.FullName} to Codacy at app.codacy.com.",
				Detail = $"""
					Codacy holds no repository named `{repository}` in `{organization}`: both the file listing and
					the repository endpoint answered 404 for that name.

					Add it at https://app.codacy.com/gh/{organization}/dashboard, then let the first analysis
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

	private RuleResult FailNothingAnalysed(RepositoryContext context, int listedFiles)
		=> Fail(
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
					["listed_files"] = listedFiles
				}
			});

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
