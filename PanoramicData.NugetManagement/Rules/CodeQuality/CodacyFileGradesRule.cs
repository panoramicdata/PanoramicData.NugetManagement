using System.Text;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Reports the individual files Codacy grades below the configured minimum level.
/// </summary>
/// <remarks>
/// Informational, always. A file's grade folds in duplication and complexity as well as issues, so a
/// file can grade F in a repository with zero open issues — which is exactly what CQ-03 used to
/// report as a compliance failure, in the words "minimum file grade F, total issues 0". That named
/// no file, gave no cause, and read as a contradiction of the Codacy issues page. Poor file grades
/// are worth seeing and are not a gate, so this rule never fails a repository.
/// </remarks>
public sealed class CodacyFileGradesRule : RuleBase
{
	private readonly ICodacyFileGradeService _fileGradeService;

	/// <summary>
	/// The number of files listed in the failure message before it is summarised.
	/// </summary>
	private const int _messageFileLimit = 3;

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyFileGradesRule"/> class.
	/// </summary>
	public CodacyFileGradesRule()
		: this(new CodacyFileGradeService())
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyFileGradesRule"/> class with an explicit
	/// file grade service (for testing).
	/// </summary>
	public CodacyFileGradesRule(ICodacyFileGradeService fileGradeService)
	{
		_fileGradeService = fileGradeService;
	}

	/// <inheritdoc />
	public override string RuleId => "CQ-06";

	/// <inheritdoc />
	public override string RuleName => "Codacy file grades";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var codacy = context.Options.Codacy;
		if (codacy is null || string.IsNullOrWhiteSpace(codacy.ApiToken))
		{
			return NotApplicable("Codacy file grade analysis is not configured for this repository.");
		}

		var parts = context.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return Pass($"Repository full name '{context.FullName}' is not in 'organization/repository' form; skipping Codacy file grade lookup.");
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
			// A broken integration is CQ-03's finding. Repeating it here would put the same problem in
			// front of the reader twice, which is the habit this rule exists to break.
			return Pass($"Codacy file grades could not be retrieved ({ex.Message}).");
		}

		// Whether the repository is in Codacy at all, and whether anything has been analysed, are both
		// CQ-03's question and are answered there.
		if (!report.IsTracked)
		{
			return Pass("Repository is not tracked by Codacy yet (no file grades to report).");
		}

		var graded = report.GradedFiles.ToList();
		if (graded.Count == 0)
		{
			return Pass($"Codacy has graded no files on {context.DefaultBranch} — see CQ-03.");
		}

		var minimumRank = GetLevelRank(codacy.MinimumLevel);
		var belowMinimum = graded
			.Select(file => (File: file, Level: ParseLevel(file.GradeLetter)))
			.Where(entry => GetLevelRank(entry.Level) < minimumRank)
			.OrderBy(entry => GetLevelRank(entry.Level))
			.ThenBy(entry => entry.File.Path, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (belowMinimum.Count == 0)
		{
			return Pass($"All {graded.Count} graded file(s) meet the minimum grade of {codacy.MinimumLevel}.");
		}

		var named = string.Join(", ", belowMinimum
			.Take(_messageFileLimit)
			.Select(entry => $"{entry.File.Path} ({entry.Level})"));

		var message = belowMinimum.Count > _messageFileLimit
			? $"{belowMinimum.Count} file(s) graded below {codacy.MinimumLevel}: {named}, and {belowMinimum.Count - _messageFileLimit} more."
			: $"{belowMinimum.Count} file(s) graded below {codacy.MinimumLevel}: {named}.";

		return Fail(message, new RuleAdvisory
		{
			Summary = $"Improve {belowMinimum.Count} file(s) graded below {codacy.MinimumLevel} in {context.FullName}.",
			Detail = BuildDetail(context, codacy.MinimumLevel, belowMinimum),
			Data = new()
			{
				["files_below_minimum"] = belowMinimum.Count,
				["graded_files"] = graded.Count,
				["required_min_grade"] = codacy.MinimumLevel.ToString(),
				["worst_grade"] = belowMinimum[0].Level.ToString(),
				["files"] = belowMinimum
					.Select(entry => new Dictionary<string, object?>
					{
						["path"] = entry.File.Path,
						["grade_letter"] = entry.Level.ToString(),
						["grade"] = entry.File.Grade,
						["total_issues"] = entry.File.TotalIssues,
						["complexity"] = entry.File.Complexity,
						["lines_of_code"] = entry.File.LinesOfCode
					})
					.ToList()
			}
		});
	}

	/// <summary>
	/// Builds the markdown an AI session reads, so it need not re-fetch anything from Codacy.
	/// </summary>
	private static string BuildDetail(
		RepositoryContext context,
		CodacyLevel minimumLevel,
		List<(CodacyFileGrade File, CodacyLevel Level)> belowMinimum)
	{
		var detail = new StringBuilder();
		detail.AppendLine($"Codacy grades these files in `{context.FullName}` below `{minimumLevel}` on `{context.DefaultBranch}`:");
		detail.AppendLine();
		detail.AppendLine("| File | Grade | Score | Issues | Complexity | Lines |");
		detail.AppendLine("| --- | --- | --- | --- | --- | --- |");

		foreach (var (file, level) in belowMinimum)
		{
			detail.AppendLine(
				$"| `{file.Path}` | {level} | {file.Grade}/100 | {file.TotalIssues} | "
				+ $"{file.Complexity?.ToString() ?? "—"} | {file.LinesOfCode?.ToString() ?? "—"} |");
		}

		detail.AppendLine();
		detail.AppendLine(
			"A file's grade is not its issue count: Codacy also folds duplication and complexity into it, "
			+ "so a file listing zero issues can still grade F. Where the issue count is zero, look for "
			+ "duplicated blocks first — repeated test setup and copy-pasted fixtures are the usual cause. "
			+ "The per-file breakdown, including the duplication percentage, is on the file's page in Codacy.");
		detail.AppendLine();
		detail.AppendLine("This is reported for information and does not affect compliance.");

		return detail.ToString();
	}

	/// <summary>
	/// Reads Codacy's grade letter. A letter we cannot parse is a grade we do not understand rather
	/// than a good one, so it stays conservative.
	/// </summary>
	private static CodacyLevel ParseLevel(string? gradeLetter)
		=> Enum.TryParse<CodacyLevel>(gradeLetter?.Trim(), ignoreCase: true, out var level)
			? level
			: CodacyLevel.F;

	private static int GetLevelRank(CodacyLevel level)
		=> level switch
		{
			CodacyLevel.A => 6,
			CodacyLevel.B => 5,
			CodacyLevel.C => 4,
			CodacyLevel.D => 3,
			CodacyLevel.E => 2,
			_ => 1
		};
}
