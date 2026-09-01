using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Surfaces the actual open Codacy issues for a repository (not just the pass/fail quality gate),
/// so they appear in the identified-issues list and—via the advisory—in the AI remediation prompt.
/// </summary>
/// <remarks>
/// Informational by default: when issues exist they are reported as a <see cref="AssessmentSeverity.Warning"/>
/// and do not fail the compliance gate, unless a hard budget (<see cref="CodacyOptions.MaxIssueCount"/> &gt; 0)
/// is breached, in which case the result is escalated to <see cref="AssessmentSeverity.Error"/>.
/// </remarks>
public sealed class CodacyIssuesRule : RuleBase, IRemotelyGraded
{
	private readonly ICodacyIssueService _issueService;

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyIssuesRule"/> class.
	/// </summary>
	public CodacyIssuesRule()
		: this(new CodacyIssueService())
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="CodacyIssuesRule"/> class with an explicit issue service (for testing).
	/// </summary>
	public CodacyIssuesRule(ICodacyIssueService issueService)
	{
		_issueService = issueService;
	}

	/// <inheritdoc />
	public override string RuleId => "CQ-05";

	/// <inheritdoc />
	public override string RuleName => "Codacy issues";

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
			return NotApplicable("Codacy issue analysis is not configured for this repository.");
		}

		var parts = context.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return Pass($"Repository full name '{context.FullName}' is not in 'organization/repository' form; skipping Codacy issue lookup.");
		}

		CodacyRepositoryReport report;
		try
		{
			report = await _issueService
				.GetReportAsync(codacy.ApiToken!, parts[0], parts[1], context.CurrentBranch ?? context.DefaultBranch, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Don't fail the assessment because Codacy was unreachable; report as a non-blocking note.
			return Pass($"Codacy issues could not be retrieved ({ex.Message}).");
		}

		if (!report.IsTracked)
		{
			return Pass("Repository is not tracked by Codacy yet (no issues to report).");
		}

		var issues = report.Issues;
		if (issues.Count == 0)
		{
			return Pass("Codacy reports no open issues.");
		}

		var summary = CodacyReportFormatter.BuildSummary(issues);
		var detail = CodacyReportFormatter.BuildDetailMarkdown(context.FullName, issues);
		var data = CodacyReportFormatter.BuildAdvisoryData(issues);

		var hardCap = codacy.MaxIssueCount;
		var breached = hardCap > 0 && issues.Count > hardCap;
		var severity = breached ? AssessmentSeverity.Error : AssessmentSeverity.Warning;

		data["max_issues"] = hardCap;
		data["budget_breached"] = breached;

		var message = breached
			? $"{summary} Exceeds the configured budget of {hardCap}."
			: summary;

		return new RuleResult
		{
			RuleId = RuleId,
			RuleName = RuleName,
			Category = Category,
			Severity = severity,
			Passed = false,
			Message = message,
#pragma warning disable CS0618 // Type or member is obsolete
			Remediation = "Review and resolve the Codacy issues listed in the advisory.",
#pragma warning restore CS0618
			Advisory = new RuleAdvisory
			{
				Summary = $"Resolve {issues.Count} Codacy issue(s) in {context.FullName}.",
				Detail = detail,
				Data = data
			}
		};
	}
}
