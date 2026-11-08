using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Requires Codacy's repository grade on the default branch to meet <see cref="CodacyOptions.MinimumLevel"/>
/// (A by default), because policy is that every published package holds an A.
/// </summary>
/// <remarks>
/// The work that raises the grade is CQ-05's issues and CQ-06's files; this rule is the verdict.
/// </remarks>
public sealed class CodacyRepositoryGradeRule : RuleBase, IRemotelyGraded
{
	private readonly ICodacyRepositoryGradeService _gradeService;

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyRepositoryGradeRule"/> class.
	/// </summary>
	public CodacyRepositoryGradeRule()
		: this(new CodacyRepositoryGradeService())
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyRepositoryGradeRule"/> class with an explicit grade service (for testing).
	/// </summary>
	public CodacyRepositoryGradeRule(ICodacyRepositoryGradeService gradeService)
	{
		_gradeService = gradeService;
	}

	/// <inheritdoc />
	public override string RuleId => "CQ-08";

	/// <inheritdoc />
	public override string RuleName => "Codacy repository grade";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var codacy = context.Options.Codacy;
		if (codacy is null || string.IsNullOrWhiteSpace(codacy.ApiToken))
		{
			return NotApplicable("Codacy grade analysis is not configured for this repository.");
		}

		var parts = context.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return Pass($"Repository full name '{context.FullName}' is not in 'organization/repository' form; skipping Codacy grade lookup.");
		}

		CodacyRepositoryGrade grade;
		try
		{
			grade = await _gradeService
				.GetGradeAsync(codacy.ApiToken!, parts[0], parts[1], context.DefaultBranch, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// An unreachable Codacy is CQ-03's finding, not a grade.
			return Pass($"Codacy grade could not be retrieved ({ex.Message}).");
		}

		if (!grade.IsTracked)
		{
			return Pass("Repository is not tracked by Codacy yet (no grade to report); see CQ-03.");
		}

		if (!grade.IsBranchAnalysed)
		{
			return BranchNotAnalysed(context, grade);
		}

		if (string.IsNullOrWhiteSpace(grade.GradeLetter))
		{
			return Pass($"Codacy has not graded {context.DefaultBranch} yet.");
		}

		var level = ParseLevel(grade.GradeLetter);
		var description = $"{grade.GradeLetter} ({grade.Grade})";
		return level >= codacy.MinimumLevel
			? Pass($"Codacy grades {context.DefaultBranch} {description}, meeting the minimum of {codacy.MinimumLevel}.")
			: BelowMinimum(context, grade, codacy.MinimumLevel, description);
	}

	private RuleResult BelowMinimum(RepositoryContext context, CodacyRepositoryGrade grade, CodacyLevel minimum, string description)
	{
		var data = new Dictionary<string, object>
		{
			["grade"] = grade.Grade ?? 0,
			["grade_letter"] = grade.GradeLetter!,
			["minimum_level"] = minimum.ToString(),
			["branch"] = context.DefaultBranch,
			["issues_percentage"] = grade.IssuesPercentage ?? 0,
			["complex_files_percentage"] = grade.ComplexFilesPercentage ?? 0,
			["duplication_percentage"] = grade.DuplicationPercentage ?? 0
		};
		CodacyFreshness.AddData(data, grade.AnalysisState);

		return Fail(
			$"Codacy grades {context.DefaultBranch} {description}; published packages must be graded {minimum} or better.",
			new RuleAdvisory
			{
				Summary = $"Raise the Codacy grade of {context.FullName} from {grade.GradeLetter} to {minimum}.",
				Detail = $"Codacy grades `{context.DefaultBranch}` {description}: {grade.IssuesPercentage ?? 0}% of lines have issues, " +
					$"{grade.ComplexFilesPercentage ?? 0}% of files are complex and {grade.DuplicationPercentage ?? 0}% are duplicated. " +
					"Fix the issues CQ-05 lists and the low-graded files CQ-06 lists, then re-assess once Codacy has re-analysed the branch. " +
					$"Dashboard: https://app.codacy.com/gh/{context.FullName}/dashboard",
				Data = data
			});
	}

	private RuleResult BranchNotAnalysed(RepositoryContext context, CodacyRepositoryGrade grade)
	{
		var codacyBranch = grade.CodacyDefaultBranch ?? "another branch";
		return Fail(
			$"Codacy has no analysis of the default branch {context.DefaultBranch}; it grades {codacyBranch}.",
			new RuleAdvisory
			{
				Summary = $"Set Codacy's default branch for {context.FullName} to {context.DefaultBranch}.",
				Detail = $"GitHub's default branch is `{context.DefaultBranch}` but Codacy grades `{codacyBranch}`, so the grade shown " +
					"does not describe the code that ships. Change the default branch in Codacy's repository settings " +
					"(or with the codacy skill's set-default-branch), then re-assess once it has analysed the branch.",
				Data = new Dictionary<string, object>
				{
					["branch"] = context.DefaultBranch,
					["codacy_default_branch"] = codacyBranch
				}
			});
	}

	/// <summary>
	/// An unparseable letter is treated as the worst grade rather than a good one.
	/// </summary>
	private static CodacyLevel ParseLevel(string? gradeLetter)
		=> Enum.TryParse<CodacyLevel>(gradeLetter?.Trim(), ignoreCase: true, out var level)
			? level
			: CodacyLevel.F;
}
