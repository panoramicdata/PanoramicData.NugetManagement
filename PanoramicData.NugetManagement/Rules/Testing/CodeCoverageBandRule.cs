using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Grades a repository's line coverage on the estate's four-band scale.
/// </summary>
/// <remarks>
/// The bands are the colours the dashboard already draws: <see cref="AssessmentSeverity.Error"/> is
/// red, <see cref="AssessmentSeverity.Warning"/> amber and <see cref="AssessmentSeverity.Info"/>
/// blue. GREEN sits at 60% to agree with the coverage gate Codacy already applies across this
/// organisation, so the dashboard and Codacy cannot disagree about whether a repository is good
/// enough.
/// <para>
/// Distinct from TST-07, which watches the direction of travel against each repository's own best
/// figure. This one states where the repository stands on one scale shared by the whole estate.
/// </para>
/// </remarks>
public class CodeCoverageBandRule : RuleBase
{
	/// <summary>Coverage at or above this is GREEN. Matches Codacy's minCoveragePercentage.</summary>
	private const double GreenThreshold = 60;

	/// <summary>Coverage below this, but above zero, is AMBER.</summary>
	private const double AmberCeiling = 30;

	/// <inheritdoc />
	public override string RuleId => "TST-10";

	/// <inheritdoc />
	public override string RuleName => "Code coverage meets the estate band";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	/// <summary>
	/// The worst case this rule can report. The emitted result carries the band's own severity, which
	/// varies; this is what rule listings and filters sort on.
	/// </summary>
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.FindTestProjectFiles().Any())
		{
			// Not RED. A repository with no tests is TST-01's finding, and reporting it twice makes
			// the fix list longer without making it more informative.
			return Task.FromResult(NotApplicable("No test projects found; there is nothing to measure."));
		}

		var line = context.LineCoveragePercent;

		if (line >= GreenThreshold)
		{
			return Task.FromResult(Pass($"Line coverage is {line:N1}% — GREEN."));
		}

		var (band, severity, message) = line switch
		{
			null => ("RED", AssessmentSeverity.Error,
				"No coverage figure is available from Codacy."),
			<= 0 => ("RED", AssessmentSeverity.Error,
				"Line coverage is 0%."),
			< AmberCeiling => ("AMBER", AssessmentSeverity.Warning,
				$"Line coverage is {line:N1}% — below {AmberCeiling:N0}%."),
			_ => ("BLUE", AssessmentSeverity.Info,
				$"Line coverage is {line:N1}% — below the {GreenThreshold:N0}% needed for GREEN.")
		};

		return Task.FromResult(Band(band, severity, message, line));
	}

	/// <summary>
	/// A failing result carrying the band's own severity rather than the rule's declared one.
	/// </summary>
	/// <remarks>
	/// <see cref="RuleBase.Fail(string, RuleAdvisory)"/> stamps <see cref="Severity"/> onto every
	/// result, which is right for a rule with one outcome and wrong for a graded one. Constructing
	/// the result here is what lets one rule paint three colours.
	/// </remarks>
	private RuleResult Band(string band, AssessmentSeverity severity, string message, double? line)
		=> new()
		{
			RuleId = RuleId,
			RuleName = RuleName,
			Category = Category,
			Severity = severity,
			Passed = false,
			Message = message,
			Advisory = new RuleAdvisory
			{
				Summary = $"Raise line coverage to at least {GreenThreshold:N0}%.",
				Detail = $"""
					This repository is {band}. The estate grades line coverage in four bands: RED for no
					coverage at all, AMBER below {AmberCeiling:N0}%, BLUE below {GreenThreshold:N0}%, and
					GREEN at or above {GreenThreshold:N0}%.

					{(line is null
						? "Codacy holds no coverage for this repository, which usually means CI never uploads any."
						: $"Measured: {line:N1}% line coverage.")}

					Coverage reaches the dashboard through Codacy, so a repository that never uploads is
					RED however well tested it is. The reference implementation is the `coverage` job in
					panoramicdata/PanoramicData.NugetManagement's .github/workflows/ci.yml: it runs the
					test project with --coverage, writes Cobertura, and uploads it with
					codacy/codacy-coverage-reporter-action. Copying that job, and adding a
					CODACY_PROJECT_TOKEN secret from the repository's Codacy settings, is the fix.
					""",
				// No measured_line key at all when there is no figure, rather than a zero or a
				// sentinel: a consumer that reads 0 cannot tell "measured nothing" from "never
				// measured", and those are different findings.
				Data = line is { } measured
					? new() { ["band"] = band, ["measured_line"] = measured }
					: new() { ["band"] = band }
			}
		};
}
