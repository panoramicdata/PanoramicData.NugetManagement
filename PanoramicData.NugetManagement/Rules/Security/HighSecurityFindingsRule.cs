using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// SEC-02: open Codacy security findings at High severity.
/// </summary>
/// <remarks>
/// <see cref="AssessmentSeverity.Warning"/>, which renders amber and does not gate compliance. A
/// High finding is worth acting on but is not the blocker a Critical one is.
/// </remarks>
public sealed class HighSecurityFindingsRule : CodacySecurityRuleBase
{
	/// <summary>
	/// Initializes a new instance of the <see cref="HighSecurityFindingsRule"/> class.
	/// </summary>
	public HighSecurityFindingsRule()
		: this(new CodacySecurityService())
	{
	}

	/// <summary>
	/// Initializes a new instance with an explicit security service (for testing).
	/// </summary>
	/// <param name="securityService">The service that fetches the repository's findings.</param>
	public HighSecurityFindingsRule(ICodacySecurityService securityService)
		: base(securityService)
	{
	}

	/// <inheritdoc />
	public override string RuleId => "SEC-02";

	/// <inheritdoc />
	public override string RuleName => "High security findings";

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	protected override IReadOnlyList<CodacySecurityPriority> Priorities => [CodacySecurityPriority.High];

	/// <inheritdoc />
	protected override string BandName => "High";
}
