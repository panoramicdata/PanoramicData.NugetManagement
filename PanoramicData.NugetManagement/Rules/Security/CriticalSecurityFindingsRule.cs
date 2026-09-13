using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// SEC-01: open Codacy security findings at Critical severity.
/// </summary>
/// <remarks>
/// The only SEC rule that gates compliance. <see cref="AssessmentSeverity.Error"/> is what makes it
/// red in the tree, and red and blocking are the same lever — a repository is compliant only when it
/// has no failing Error or Critical rule. That is the intent: a Critical security finding on the
/// default branch should stop a repository counting as compliant.
/// </remarks>
public sealed class CriticalSecurityFindingsRule : CodacySecurityRuleBase
{
	/// <summary>
	/// Initializes a new instance of the <see cref="CriticalSecurityFindingsRule"/> class.
	/// </summary>
	public CriticalSecurityFindingsRule()
		: this(new CodacySecurityService())
	{
	}

	/// <summary>
	/// Initializes a new instance with an explicit security service (for testing).
	/// </summary>
	/// <param name="securityService">The service that fetches the repository's findings.</param>
	public CriticalSecurityFindingsRule(ICodacySecurityService securityService)
		: base(securityService)
	{
	}

	/// <inheritdoc />
	public override string RuleId => "SEC-01";

	/// <inheritdoc />
	public override string RuleName => "Critical security findings";

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	protected override IReadOnlyList<CodacySecurityPriority> Priorities => [CodacySecurityPriority.Critical];

	/// <inheritdoc />
	protected override string BandName => "Critical";
}
