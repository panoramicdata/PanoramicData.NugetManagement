using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Shared behaviour for the SEC rules, which split Codacy's open security findings into severity
/// bands so each reports in its own colour.
/// </summary>
/// <remarks>
/// The split is by band rather than one rule reporting everything, so the RAG colour of a finding
/// survives into the tree: <see cref="AssessmentSeverity"/> drives both the colour and the
/// compliance gate, and a single rule would have to pick one severity for findings of four.
/// <para>
/// Every band reads the same report — <see cref="ICodacySecurityService"/> memoises it — so the
/// three rules together cost one search, not three.
/// </para>
/// <para>
/// <see cref="IRemotelyGraded"/>, because Codacy grades the default branch: editing the clone cannot
/// change this rule's answer within a fix session, however correct the edit. It is deliberately not
/// <see cref="IFixedOutsideTheWorkingTree"/> — a SAST finding is real code in the tree, and fixing
/// it there is exactly right; only the confirmation has to wait for Codacy's next analysis.
/// </para>
/// </remarks>
public abstract class CodacySecurityRuleBase : RuleBase, IRemotelyGraded
{
	private readonly ICodacySecurityService _securityService;

	/// <summary>
	/// Initializes a new instance with an explicit security service (for testing).
	/// </summary>
	/// <param name="securityService">The service that fetches the repository's findings.</param>
	protected CodacySecurityRuleBase(ICodacySecurityService securityService)
	{
		_securityService = securityService;
	}

	/// <summary>
	/// The Codacy priorities this rule owns. Every open finding falls to exactly one SEC rule.
	/// </summary>
	protected abstract IReadOnlyList<CodacySecurityPriority> Priorities { get; }

	/// <summary>
	/// How this rule's band is named in prose, e.g. "Critical" or "Medium and Low".
	/// </summary>
	protected abstract string BandName { get; }

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Security;

	/// <inheritdoc />
	public override async Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var codacy = context.Options.Codacy;
		if (codacy is null || string.IsNullOrWhiteSpace(codacy.ApiToken))
		{
			return NotApplicable("Codacy security analysis is not configured for this repository.");
		}

		var parts = context.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return Pass($"Repository full name '{context.FullName}' is not in 'organization/repository' form; skipping Codacy security lookup.");
		}

		CodacySecurityReport report;
		try
		{
			report = await _securityService
				.GetReportAsync(codacy.ApiToken!, parts[0], parts[1], cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// An unreachable Codacy is not a security finding. Reporting one would cry wolf, and
			// reporting it as an Error would fail the compliance gate on an outage.
			return Pass($"Codacy security findings could not be retrieved ({ex.Message}).");
		}

		if (!report.IsTracked)
		{
			return Pass("Repository is not tracked by Codacy yet (no security findings to report).");
		}

		var findings = report.Findings
			.Where(finding => Priorities.Contains(finding.Priority))
			.ToList();

		if (findings.Count == 0)
		{
			return Pass(CodacySecurityFormatter.BuildSummary(BandName, findings));
		}

		return Fail(
			CodacySecurityFormatter.BuildSummary(BandName, findings),
			new RuleAdvisory
			{
				Summary = $"Resolve {findings.Count} {BandName} Codacy security finding(s) in {context.FullName}.",
				Detail = CodacySecurityFormatter.BuildDetailMarkdown(context.FullName, BandName, findings),
				Data = CodacySecurityFormatter.BuildAdvisoryData(BandName, findings)
			});
	}
}
