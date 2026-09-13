using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// SEC-03: open Codacy security findings below High severity.
/// </summary>
/// <remarks>
/// <see cref="AssessmentSeverity.Info"/>, which renders blue and does not gate compliance. This band
/// owns two priorities rather than one so that every open finding falls to exactly one SEC rule —
/// Codacy defines Critical, High, Medium and Low, and anything not caught here would go unreported.
/// </remarks>
public sealed class OtherSecurityFindingsRule : CodacySecurityRuleBase
{
	/// <summary>
	/// Initializes a new instance of the <see cref="OtherSecurityFindingsRule"/> class.
	/// </summary>
	public OtherSecurityFindingsRule()
		: this(new CodacySecurityService())
	{
	}

	/// <summary>
	/// Initializes a new instance with an explicit security service (for testing).
	/// </summary>
	/// <param name="securityService">The service that fetches the repository's findings.</param>
	public OtherSecurityFindingsRule(ICodacySecurityService securityService)
		: base(securityService)
	{
	}

	/// <inheritdoc />
	public override string RuleId => "SEC-03";

	/// <inheritdoc />
	public override string RuleName => "Other security findings";

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	protected override IReadOnlyList<CodacySecurityPriority> Priorities =>
		[CodacySecurityPriority.Medium, CodacySecurityPriority.Low];

	/// <inheritdoc />
	protected override string BandName => "Medium and Low";
}
