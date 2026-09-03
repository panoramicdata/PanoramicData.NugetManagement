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
public sealed class CodacyFileGradesRule : RuleBase, IRemotelyGraded
{
	private readonly ICodacyFileGradeService _fileGradeService;
	private readonly ICodacyIssueService? _issueService;

	/// <summary>
	/// The number of files listed in the failure message before it is summarised.
	/// </summary>
	private const int _messageFileLimit = 3;

	/// <summary>
	/// The number of files offered to Fix with AI as targets.
	/// </summary>
	/// <remarks>
	/// Every target becomes a queued item and a session on the shared GPU, so a repository with forty
	/// poor files must not queue forty. The worst-graded are taken, and the advisory says how many were
	/// left — a silent truncation would read as "that was all of them".
	/// </remarks>
	private const int _targetLimit = 10;

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyFileGradesRule"/> class.
	/// </summary>
	public CodacyFileGradesRule()
		: this(new CodacyFileGradeService(), new CodacyIssueService())
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyFileGradesRule"/> class with an explicit
	/// file grade service (for testing).
	/// </summary>
	public CodacyFileGradesRule(ICodacyFileGradeService fileGradeService)
		: this(fileGradeService, issueService: null)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyFileGradesRule"/> class with explicit
	/// services (for testing).
	/// </summary>
	/// <param name="fileGradeService">Supplies the per-file grades this rule reports on.</param>
	/// <param name="issueService">
	/// Supplies the issues behind those grades, or null to report grades alone. Separate from the grade
	/// service because it is a different Codacy endpoint and a different failure: grades that arrive
	/// without issues are still worth reporting.
	/// </param>
	public CodacyFileGradesRule(ICodacyFileGradeService fileGradeService, ICodacyIssueService? issueService)
	{
		_fileGradeService = fileGradeService;
		_issueService = issueService;
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

		// The issues behind the grades. Without them the finding says a file is poor and no more, and
		// whoever acts on it — a person or a model — has to go and guess which nine things Codacy meant.
		var issuesByFile = await GetIssuesByFileAsync(
			codacy.ApiToken!,
			parts[0],
			parts[1],
			context.DefaultBranch,
			cancellationToken).ConfigureAwait(false);

		var named = string.Join(", ", belowMinimum
			.Take(_messageFileLimit)
			.Select(entry => $"{entry.File.Path} ({Describe(entry.File, entry.Level)})"));

		var message = belowMinimum.Count > _messageFileLimit
			? $"{belowMinimum.Count} file(s) graded below {codacy.MinimumLevel}: {named}, and {belowMinimum.Count - _messageFileLimit} more."
			: $"{belowMinimum.Count} file(s) graded below {codacy.MinimumLevel}: {named}.";

		return Fail(message, new RuleAdvisory
		{
			Summary = $"Improve {belowMinimum.Count} file(s) graded below {codacy.MinimumLevel} in {context.FullName}.",
			Detail = BuildDetail(context, codacy.MinimumLevel, belowMinimum, issuesByFile),
			Data = new()
			{
				["files_below_minimum"] = belowMinimum.Count,
				["graded_files"] = graded.Count,
				["required_min_grade"] = codacy.MinimumLevel.ToString(),
				["worst_grade"] = belowMinimum[0].Level.ToString(),
				["files"] = belowMinimum
					.Select(entry => BuildFileData(entry.File, entry.Level, IssuesFor(issuesByFile, entry.File.Path)))
					.ToList()
			},

			// One session per file, worst first. The list is capped because each entry costs a queued
			// item and a turn on the GPU, and BuildDetail says so where the reader can see it.
			Targets =
			[
				.. belowMinimum
					.Take(_targetLimit)
					.Select(entry => BuildTarget(entry.File, entry.Level, IssuesFor(issuesByFile, entry.File.Path)))
			]
		});
	}

	/// <summary>
	/// The repository's open issues, grouped by the file they were found in.
	/// </summary>
	/// <remarks>
	/// Failure here is quiet on purpose. The grades are the finding; the issues make it actionable. An
	/// issues endpoint that is unreachable, or absent because no issue service was supplied, costs the
	/// detail and nothing else — reporting it would raise a second alarm for a problem CQ-03 already
	/// owns, and losing the grades over it would be worse still.
	/// </remarks>
	private async Task<Dictionary<string, List<CodacyIssue>>> GetIssuesByFileAsync(
		string apiToken,
		string organizationName,
		string repositoryName,
		string? branch,
		CancellationToken cancellationToken)
	{
		if (_issueService is null)
		{
			return [];
		}

		try
		{
			var report = await _issueService
				.GetReportAsync(apiToken, organizationName, repositoryName, branch, cancellationToken)
				.ConfigureAwait(false);

			return report.Issues
				.GroupBy(issue => Normalise(issue.FilePath), StringComparer.OrdinalIgnoreCase)
				.ToDictionary(
					group => group.Key,
					group => group.OrderBy(issue => issue.Line).ToList(),
					StringComparer.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return [];
		}
	}

	/// <summary>
	/// The issues found in one file, or none when Codacy attributed none to it.
	/// </summary>
	private static IReadOnlyList<CodacyIssue> IssuesFor(
		Dictionary<string, List<CodacyIssue>> issuesByFile,
		string path)
		=> issuesByFile.TryGetValue(Normalise(path), out var issues) ? issues : [];

	/// <summary>
	/// One path, one spelling. Codacy returns file paths from two endpoints and they need not agree on
	/// separators or on a leading "./", and a path that fails to match silently drops a file's issues.
	/// </summary>
	private static string Normalise(string path)
		=> path.Replace('\\', '/').TrimStart('.', '/');

	/// <summary>
	/// One file's measurements and issues, as the machine-readable half of the advisory.
	/// </summary>
	private static Dictionary<string, object?> BuildFileData(
		CodacyFileGrade file,
		CodacyLevel level,
		IReadOnlyList<CodacyIssue> issues)
		=> new()
		{
			["path"] = file.Path,
			["grade_letter"] = level.ToString(),
			["grade"] = file.Grade,
			["total_issues"] = file.TotalIssues,
			["duplication_percent"] = file.Duplication,
			["number_of_clones"] = file.NumberOfClones,
			["complexity"] = file.Complexity,
			["lines_of_code"] = file.LinesOfCode,
			["issues"] = issues
				.Select(issue => new Dictionary<string, object?>
				{
					["line"] = issue.Line,
					["pattern"] = issue.PatternId,
					["message"] = issue.Message
				})
				.ToList()
		};

	/// <summary>
	/// One file as a self-contained piece of work: what is wrong with it, and what to do about it,
	/// mentioning no other file.
	/// </summary>
	/// <remarks>
	/// Written for a session that will see this and nothing else. Which measurement drove the grade
	/// decides what the instruction is, because the three causes need three different fixes and a
	/// small model will not pick between them from a table: issues are fixed one at a time, duplication
	/// by factoring out the repeated block, complexity by splitting the method.
	/// </remarks>
	private static AdvisoryTarget BuildTarget(
		CodacyFileGrade file,
		CodacyLevel level,
		IReadOnlyList<CodacyIssue> issues)
	{
		var detail = new StringBuilder();

		if (issues.Count > 0)
		{
			detail.AppendLine($"Codacy reports these issues in {file.Path}. Fix them:");
			detail.AppendLine();

			foreach (var issue in issues)
			{
				var location = issue.Line > 0 ? $"line {issue.Line}" : "the file";
				var pattern = string.IsNullOrWhiteSpace(issue.PatternId) ? "(unknown pattern)" : issue.PatternId;

				detail.AppendLine($"- {location} — {pattern} — {issue.Message}");
			}

			detail.AppendLine();
		}

		if (file.Duplication is > 0)
		{
			detail.AppendLine(
				$"{file.Duplication}% of {file.Path} is duplicated, across "
				+ $"{file.NumberOfClones?.ToString() ?? "several"} clone(s). Find the blocks that repeat and "
				+ "factor them into one helper, called from each place the block used to be. Repeated test "
				+ "setup is the usual cause.");
			detail.AppendLine();
		}

		if (file.Complexity is > 15)
		{
			detail.AppendLine(
				$"{file.Path} has a complexity of {file.Complexity}. Find the longest method and split its "
				+ "branches into separate private methods, changing what it does in no way.");
			detail.AppendLine();
		}

		if (detail.Length == 0)
		{
			// Grade below the bar with nothing measured to explain it. Saying so is better than an
			// empty instruction, which a model fills in with whatever it feels like changing.
			detail.AppendLine(
				$"Codacy grades {file.Path} {level} but attributes no issues, duplication or complexity to "
				+ "it. There may be nothing to do here; if you cannot see a concrete problem, change nothing.");
		}

		return new AdvisoryTarget(
			file.Path,
			$"{file.Path} is graded {level} by Codacy, below the required minimum. {Describe(file, level)}.",
			detail.ToString().TrimEnd());
	}

	/// <summary>
	/// Builds the markdown an AI session reads, so it need not re-fetch anything from Codacy.
	/// </summary>
	private static string BuildDetail(
		RepositoryContext context,
		CodacyLevel minimumLevel,
		List<(CodacyFileGrade File, CodacyLevel Level)> belowMinimum,
		Dictionary<string, List<CodacyIssue>> issuesByFile)
	{
		var detail = new StringBuilder();
		detail.AppendLine($"Codacy grades these files in `{context.FullName}` below `{minimumLevel}` on `{context.DefaultBranch}`:");
		detail.AppendLine();
		detail.AppendLine("| File | Grade | Score | Issues | Duplication | Clones | Complexity | Lines |");
		detail.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

		foreach (var (file, level) in belowMinimum)
		{
			detail.AppendLine(
				$"| `{file.Path}` | {level} | {file.Grade}/100 | {file.TotalIssues} | "
				+ $"{(file.Duplication is null ? "—" : $"{file.Duplication}%")} | "
				+ $"{file.NumberOfClones?.ToString() ?? "—"} | "
				+ $"{file.Complexity?.ToString() ?? "—"} | {file.LinesOfCode?.ToString() ?? "—"} |");
		}

		detail.AppendLine();

		// The issues themselves, per file. A grade says a file is poor; this says what Codacy objected
		// to and where, which is the difference between a fix and a guess.
		foreach (var (file, _) in belowMinimum)
		{
			var issues = IssuesFor(issuesByFile, file.Path);

			if (issues.Count == 0)
			{
				continue;
			}

			detail.AppendLine($"### `{file.Path}` — {issues.Count} open issue(s)");
			detail.AppendLine();

			foreach (var issue in issues)
			{
				var location = issue.Line > 0 ? $"line {issue.Line}" : "file";
				var pattern = string.IsNullOrWhiteSpace(issue.PatternId) ? "(unknown pattern)" : issue.PatternId;

				detail.AppendLine($"- {location} — `{pattern}` — {issue.Message}");
			}

			detail.AppendLine();
		}

		if (belowMinimum.Count > _targetLimit)
		{
			detail.AppendLine(
				$"Fix with AI will offer the worst {_targetLimit} of these {belowMinimum.Count} files, one "
				+ "session each. The rest are listed above and are not queued.");
			detail.AppendLine();
		}

		detail.AppendLine(
			"A file's grade is not its issue count: Codacy also folds duplication and complexity into it, "
			+ "so a file listing zero issues can still grade F. Where duplication is the figure driving the "
			+ "grade, the fix is to factor out the repeated blocks — repeated test setup and copy-pasted "
			+ "fixtures are the usual cause.");
		detail.AppendLine();
		detail.AppendLine("This is reported for information and does not affect compliance.");

		return detail.ToString();
	}

	/// <summary>
	/// Describes a file's grade and, where Codacy measured one, the thing driving it. A grade with no
	/// cause attached is what made "minimum file grade F, total issues 0" unanswerable.
	/// </summary>
	private static string Describe(CodacyFileGrade file, CodacyLevel level)
	{
		// Duplication is reported ahead of issues because it is the component a reader cannot infer:
		// the issue count is already on Codacy's issues page, and its being zero is exactly what makes
		// a poor grade look like a mistake.
		if (file.Duplication is > 0)
		{
			return $"{level}, {file.Duplication}% duplication";
		}

		return file.TotalIssues > 0
			? $"{level}, {file.TotalIssues} issue(s)"
			: level.ToString();
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
